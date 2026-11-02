using System.Text;
using Dapper;
using Lims.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lims.Audit;

public sealed class AuditExportOptions
{
    /// <summary>
    /// Directory on a WORM/immutable share managed by another team. Syslog over TLS (RFC 5425) is the
    /// other design option and is not in this MVP build.
    /// </summary>
    public string? FileSinkDirectory { get; set; }
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan AnchorInterval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Ships every audit event as a JSON line to an external sink within a minute, and writes hourly chain anchors.</summary>
public sealed class AuditExporter(Db db, IOptions<AuditExportOptions> options, ILogger<AuditExporter> logger) : BackgroundService
{
    private const string Destination = "file";
    private DateTimeOffset _lastAnchor = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;
        if (string.IsNullOrWhiteSpace(opt.FileSinkDirectory))
        {
            logger.LogWarning("Audit export sink is not configured; audit events stay only in the database");
            return;
        }
        Directory.CreateDirectory(opt.FileSinkDirectory);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ExportOnceAsync(opt, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Audit export failed"); }
            await Task.Delay(opt.Interval, stoppingToken);
        }
    }

    public async Task ExportOnceAsync(AuditExportOptions opt, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var last = await conn.ExecuteScalarAsync<long?>("SELECT last_seq FROM audit.export_state WHERE destination=@d", new { d = Destination }) ?? 0;
        var rows = (await conn.QueryAsync("""
            SELECT seq, occurred_at, actor, action, entity_type, entity_id, accession, correlation_id, reason,
                   encode(prev_hash,'hex') AS prev_hash, encode(hash,'hex') AS hash
            FROM audit.audit_event WHERE seq > @last ORDER BY seq LIMIT 1000
            """, new { last })).ToList();
        if (rows.Count > 0)
        {
            var file = Path.Combine(opt.FileSinkDirectory!, $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var sb = new StringBuilder();
            foreach (var r in rows) sb.AppendLine(Json.Serialize((IDictionary<string, object?>)r));
            await File.AppendAllTextAsync(file, sb.ToString(), ct);
            long newLast = rows[^1].seq;
            await conn.ExecuteAsync("""
                INSERT INTO audit.export_state (destination, last_seq) VALUES (@d, @s)
                ON CONFLICT (destination) DO UPDATE SET last_seq = EXCLUDED.last_seq, updated_at = now()
                """, new { d = Destination, s = newLast });
        }

        if (DateTimeOffset.UtcNow - _lastAnchor >= opt.AnchorInterval)
        {
            var head = await conn.QuerySingleOrDefaultAsync<(long Seq, byte[] Hash)?>("SELECT seq, hash FROM audit.audit_event ORDER BY seq DESC LIMIT 1");
            if (head is { } h)
            {
                await File.AppendAllTextAsync(Path.Combine(opt.FileSinkDirectory!, "anchors.log"),
                    $"{DateTimeOffset.UtcNow:O} seq={h.Seq} hash={Convert.ToHexString(h.Hash)}{Environment.NewLine}", ct);
                await conn.ExecuteAsync("INSERT INTO audit.chain_anchor (seq, hash, destination) VALUES (@s, @h, @d)",
                    new { s = h.Seq, h = h.Hash, d = Destination });
            }
            _lastAnchor = DateTimeOffset.UtcNow;
        }
    }
}
