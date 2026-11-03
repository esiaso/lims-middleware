using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Dapper;
using Lims.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lims.Audit;

public sealed class SyslogOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 6514;
    /// <summary>Optional pin: SHA-1 thumbprint of the collector certificate (or of its issuing CA).</summary>
    public string? ServerCertificateThumbprint { get; set; }
    public string? ClientCertificatePath { get; set; }
    public string? ClientCertificatePassword { get; set; }
    public string AppName { get; set; } = "lims-middleware";
}

public sealed class AuditExportOptions
{
    /// <summary>Directory on a WORM/immutable share managed by another team.</summary>
    public string? FileSinkDirectory { get; set; }
    /// <summary>Syslog over TLS, RFC 5424 messages with RFC 5425 octet-counting framing.</summary>
    public SyslogOptions Syslog { get; set; } = new();
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan AnchorInterval { get; set; } = TimeSpan.FromHours(1);
    /// <summary>Secret (at least 32 characters; dpapi-machine:, file: or credential: reference) for signing audit exports from the Audit screen.</summary>
    public string? SigningKey { get; set; }
}

public sealed record AuditExportRow(long Seq, DateTime OccurredAt, string Actor, string Action, string EntityType, string? EntityId,
    string? Accession, string? CorrelationId, string? Reason, string PrevHash, string Hash);

public interface IAuditSink
{
    string Name { get; }
    Task SendAsync(IReadOnlyList<AuditExportRow> rows, CancellationToken ct);
    Task AnchorAsync(long seq, string hashHex, CancellationToken ct);
}

public sealed class FileAuditSink(string directory) : IAuditSink
{
    public string Name => "file";

    public async Task SendAsync(IReadOnlyList<AuditExportRow> rows, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var sb = new StringBuilder();
        foreach (var r in rows) sb.AppendLine(Json.Serialize(r));
        await File.AppendAllTextAsync(Path.Combine(directory, $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl"), sb.ToString(), ct);
    }

    public Task AnchorAsync(long seq, string hashHex, CancellationToken ct) =>
        File.AppendAllTextAsync(Path.Combine(directory, "anchors.log"), $"{DateTimeOffset.UtcNow:O} seq={seq} hash={hashHex}{Environment.NewLine}", ct);
}

/// <summary>RFC 5425 syslog over TLS. Each audit event is one RFC 5424 message with the event as JSON.</summary>
public sealed class SyslogTlsAuditSink(SyslogOptions o) : IAuditSink, IDisposable
{
    private TcpClient? _client;
    private SslStream? _ssl;
    public string Name => "syslog";

    private async Task<SslStream> ConnectAsync(CancellationToken ct)
    {
        if (_ssl is not null && _client?.Connected == true) return _ssl;
        Dispose();
        _client = new TcpClient();
        await _client.ConnectAsync(o.Host!, o.Port, ct);
        _ssl = new SslStream(_client.GetStream(), false, ValidateServer);
        var opts = new SslClientAuthenticationOptions
        {
            TargetHost = o.Host!,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        if (!string.IsNullOrWhiteSpace(o.ClientCertificatePath))
            opts.ClientCertificates = [X509CertificateLoader.LoadPkcs12FromFile(o.ClientCertificatePath, o.ClientCertificatePassword)];
        await _ssl.AuthenticateAsClientAsync(opts, ct);
        return _ssl;
    }

    private bool ValidateServer(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (string.IsNullOrWhiteSpace(o.ServerCertificateThumbprint)) return errors == SslPolicyErrors.None;
        var pin = o.ServerCertificateThumbprint.Replace(" ", "", StringComparison.Ordinal);
        if (cert is X509Certificate2 c2 && string.Equals(c2.Thumbprint, pin, StringComparison.OrdinalIgnoreCase)) return true;
        // Pinned CA: the chain must contain it (name mismatch is still refused).
        return chain?.ChainElements.Any(e => string.Equals(e.Certificate.Thumbprint, pin, StringComparison.OrdinalIgnoreCase)) == true
               && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0;
    }

    public static string Format(AuditExportRow r, string appName, string host)
    {
        // <PRI>VERSION TIMESTAMP HOSTNAME APP-NAME PROCID MSGID SD MSG ; facility 13 (log audit), severity 5 (notice) => 109
        var ts = DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        return $"<109>1 {ts} {host} {appName} - {Sanitize(r.Action)} [lims@32473 seq=\"{r.Seq}\" hash=\"{r.Hash}\"] {Json.Serialize(r)}";
    }

    private static string Sanitize(string s) => new(s.Select(c => c is > ' ' and <= '~' ? c : '_').Take(32).ToArray());

    public static byte[] Frame(string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        return Encoding.ASCII.GetBytes(body.Length.ToString(CultureInfo.InvariantCulture) + " ").Concat(body).ToArray();
    }

    public async Task SendAsync(IReadOnlyList<AuditExportRow> rows, CancellationToken ct)
    {
        try
        {
            var s = await ConnectAsync(ct);
            foreach (var r in rows) await s.WriteAsync(Frame(Format(r, o.AppName, Environment.MachineName)), ct);
            await s.FlushAsync(ct);
        }
        catch { Dispose(); throw; }
    }

    public async Task AnchorAsync(long seq, string hashHex, CancellationToken ct)
    {
        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        var s = await ConnectAsync(ct);
        await s.WriteAsync(Frame($"<109>1 {ts} {Environment.MachineName} {o.AppName} - audit.anchor [lims@32473 seq=\"{seq}\" hash=\"{hashHex}\"] chain head"), ct);
        await s.FlushAsync(ct);
    }

    public void Dispose()
    {
        _ssl?.Dispose(); _ssl = null;
        _client?.Dispose(); _client = null;
    }
}

/// <summary>
/// Ships every audit event to each configured external sink within a minute, tracking progress per sink,
/// and writes hourly chain anchors (design 9.6).
/// </summary>
public sealed class AuditExporter(Db db, IOptions<AuditExportOptions> options, ILogger<AuditExporter> logger) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _lastAnchor = new();

    public static IReadOnlyList<IAuditSink> CreateSinks(AuditExportOptions o)
    {
        var sinks = new List<IAuditSink>();
        if (!string.IsNullOrWhiteSpace(o.FileSinkDirectory)) sinks.Add(new FileAuditSink(o.FileSinkDirectory));
        if (!string.IsNullOrWhiteSpace(o.Syslog.Host)) sinks.Add(new SyslogTlsAuditSink(o.Syslog));
        return sinks;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        var sinks = CreateSinks(opt);
        if (sinks.Count == 0)
        {
            logger.LogWarning("No audit export sink is configured; audit events stay only in the database");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var sink in sinks)
            {
                try { await ExportOnceAsync(sink, opt, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Audit export to {Sink} failed", sink.Name); }
            }
            await Task.Delay(opt.Interval, stoppingToken);
        }
    }

    public async Task<int> ExportOnceAsync(IAuditSink sink, AuditExportOptions opt, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var last = await conn.ExecuteScalarAsync<long?>("SELECT last_seq FROM audit.export_state WHERE destination=@d", new { d = sink.Name }) ?? 0;
        var rows = (await conn.QueryAsync<AuditExportRow>("""
            SELECT seq, occurred_at AS OccurredAt, actor, action, entity_type AS EntityType, entity_id AS EntityId, accession,
                   correlation_id AS CorrelationId, reason, encode(prev_hash,'hex') AS PrevHash, encode(hash,'hex') AS Hash
            FROM audit.audit_event WHERE seq > @last ORDER BY seq LIMIT 1000
            """, new { last })).ToList();
        if (rows.Count > 0)
        {
            await sink.SendAsync(rows, ct);
            await conn.ExecuteAsync("""
                INSERT INTO audit.export_state (destination, last_seq) VALUES (@d, @s)
                ON CONFLICT (destination) DO UPDATE SET last_seq = EXCLUDED.last_seq, updated_at = now()
                """, new { d = sink.Name, s = rows[^1].Seq });
        }

        if (!_lastAnchor.TryGetValue(sink.Name, out var la) || DateTimeOffset.UtcNow - la >= opt.AnchorInterval)
        {
            var head = await conn.QuerySingleOrDefaultAsync<(long Seq, byte[] Hash)?>("SELECT seq, hash FROM audit.audit_event ORDER BY seq DESC LIMIT 1");
            if (head is { } h)
            {
                await sink.AnchorAsync(h.Seq, Convert.ToHexString(h.Hash).ToLowerInvariant(), ct);
                await conn.ExecuteAsync("INSERT INTO audit.chain_anchor (seq, hash, destination) VALUES (@s, @h, @d)",
                    new { s = h.Seq, h = h.Hash, d = sink.Name });
            }
            _lastAnchor[sink.Name] = DateTimeOffset.UtcNow;
        }
        return rows.Count;
    }
}
