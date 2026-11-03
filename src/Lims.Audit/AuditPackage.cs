using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Lims.Platform;
using Microsoft.Extensions.Options;

namespace Lims.Audit;

/// <summary>Audit search filters (design 8): accession, patient, instrument, user, date range, correlation ID, action type.</summary>
public sealed record AuditFilter(string? Accession = null, string? Patient = null, string? Instrument = null, string? Actor = null,
    string? CorrelationId = null, string? EntityId = null, string? Action = null, DateTime? From = null, DateTime? To = null);

public sealed record AuditEventRow(long Seq, DateTime OccurredAt, string Actor, string? ActorAddress, string Action, string EntityType, string? EntityId,
    string? Accession, string? PatientId, string? CorrelationId, string? Reason, string? Before, string? After, byte[] PrevHash, byte[] Hash);

public static class AuditSearch
{
    /// <summary>
    /// Instrument matches events about the instrument itself and events that carry the correlation ID of a message from it.
    /// Action matches exactly, or as a prefix when it ends with a dot ("result.").
    /// </summary>
    public static async Task<IReadOnlyList<AuditEventRow>> FindAsync(Db db, AuditFilter f, int limit, bool newestFirst, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var sql = $"""
            SELECT seq, occurred_at AS OccurredAt, actor, actor_address AS ActorAddress, action, entity_type AS EntityType, entity_id AS EntityId,
                   accession, patient_id AS PatientId, correlation_id AS CorrelationId, reason,
                   before_json::text AS Before, after_json::text AS After, prev_hash AS PrevHash, hash
            FROM audit.audit_event
            WHERE (@Accession::text IS NULL OR accession = @Accession)
              AND (@Patient::text IS NULL OR patient_id = @Patient)
              AND (@Actor::text IS NULL OR actor = @Actor)
              AND (@CorrelationId::text IS NULL OR correlation_id = @CorrelationId)
              AND (@EntityId::text IS NULL OR entity_id = @EntityId)
              AND (@Action::text IS NULL OR action = @Action OR (right(@Action, 1) = '.' AND starts_with(action, @Action)))
              AND (@Instrument::text IS NULL OR (entity_type = 'instrument' AND entity_id = @Instrument)
                   OR correlation_id IN (SELECT correlation_id FROM messaging.inbox_message WHERE source = 'instrument' AND channel = @Instrument))
              AND (@From::timestamptz IS NULL OR occurred_at >= @From)
              AND (@To::timestamptz IS NULL OR occurred_at < @To + interval '1 day')
            ORDER BY seq {(newestFirst ? "DESC" : "ASC")} LIMIT @limit
            """;
        var rows = await conn.QueryAsync<AuditEventRow>(new CommandDefinition(sql, new
        {
            Accession = Nz(f.Accession), Patient = Nz(f.Patient), Actor = Nz(f.Actor), CorrelationId = Nz(f.CorrelationId), EntityId = Nz(f.EntityId),
            Action = Nz(f.Action), Instrument = Nz(f.Instrument), f.From, f.To, limit,
        }, cancellationToken: ct));
        return rows.ToList();
    }

    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Signed audit export (design 8). A ZIP holding the data file (audit.csv or audit.json), manifest.json and manifest.sig.
/// manifest.json records the filter, row range, the SHA-256 of the data file and the live chain head at export time;
/// manifest.sig is HMAC-SHA256 over the manifest bytes with the AuditExport:SigningKey secret. Every row carries its own
/// prev_hash and hash, so each can also be checked against the hash chain. Verify with "LimsMiddleware verify-export".
/// </summary>
public sealed class AuditPackage(Db db, AuditWriter audit, IOptions<AuditExportOptions> options)
{
    public const int MaxRows = 200_000;

    public async Task<(byte[] Zip, string FileName)> ExportAsync(AuditFilter filter, string format, string user, CancellationToken ct)
    {
        var key = SigningKey(options.Value.SigningKey);
        format = format.Equals("json", StringComparison.OrdinalIgnoreCase) ? "json" : "csv";
        var rows = await AuditSearch.FindAsync(db, filter, MaxRows + 1, newestFirst: false, ct);
        if (rows.Count > MaxRows) throw new WorkflowException($"More than {MaxRows} events match: narrow the filter (date range)");

        var data = format == "json" ? ToJson(rows) : ToCsv(rows);
        (long Seq, byte[] Hash) head;
        await using (var conn = await db.OpenAsync(ct))
            head = await conn.QuerySingleOrDefaultAsync<(long, byte[])>("SELECT seq, hash FROM audit.audit_event ORDER BY seq DESC LIMIT 1");
        var exportedAt = DateTimeOffset.UtcNow;
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format_version = 1,
            data_file = $"audit.{format}",
            data_sha256 = Convert.ToHexStringLower(SHA256.HashData(data)),
            row_count = rows.Count,
            first_seq = rows.FirstOrDefault()?.Seq,
            last_seq = rows.LastOrDefault()?.Seq,
            filter,
            chain_head = new { seq = head.Seq, hash = head.Hash is null ? null : Convert.ToHexStringLower(head.Hash) },
            exported_by = user,
            exported_at = exportedAt,
            signature = new { algorithm = "HMAC-SHA256", key_id = KeyId(key), file = "manifest.sig" },
        }, new JsonSerializerOptions { WriteIndented = true });
        var sig = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(HMACSHA256.HashData(key, manifest)));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, $"audit.{format}", data);
            Add(zip, "manifest.json", manifest);
            Add(zip, "manifest.sig", sig);
        }
        await audit.WriteAsync(db, new AuditEntry(user, "audit.exported", "audit_event", null,
            After: new { format, rows = rows.Count, first = rows.FirstOrDefault()?.Seq, last = rows.LastOrDefault()?.Seq, filter }), ct);
        return (ms.ToArray(), $"audit-{exportedAt:yyyyMMdd-HHmmss}.zip");
    }

    public sealed record VerifyResult(bool Valid, string Detail);

    /// <summary>Checks manifest.sig against the key and the data file against the manifest's SHA-256.</summary>
    public static VerifyResult Verify(Stream zipStream, string signingKey)
    {
        var key = SigningKey(signingKey);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        byte[] Read(string name)
        {
            var e = zip.GetEntry(name) ?? throw new InvalidDataException($"{name} missing");
            using var s = e.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }
        var manifest = Read("manifest.json");
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(key, manifest));
        var sig = Encoding.ASCII.GetString(Read("manifest.sig")).Trim();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig)))
            return new VerifyResult(false, "manifest signature does not match (wrong key, or the manifest was changed)");
        using var doc = JsonDocument.Parse(manifest);
        var dataFile = doc.RootElement.GetProperty("data_file").GetString()!;
        var dataHash = Convert.ToHexStringLower(SHA256.HashData(Read(dataFile)));
        if (dataHash != doc.RootElement.GetProperty("data_sha256").GetString())
            return new VerifyResult(false, $"{dataFile} does not match the signed manifest");
        return new VerifyResult(true, $"{doc.RootElement.GetProperty("row_count").GetInt32()} rows, signature valid");
    }

    private static byte[] SigningKey(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || configured.Length < 32)
            throw new WorkflowException("Audit export signing is not configured: set AuditExport:SigningKey (a secret of at least 32 characters)");
        return Encoding.UTF8.GetBytes(configured);
    }

    private static string KeyId(byte[] key) => Convert.ToHexStringLower(SHA256.HashData(key))[..16];

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(bytes);
    }

    private static byte[] ToJson(IReadOnlyList<AuditEventRow> rows) =>
        JsonSerializer.SerializeToUtf8Bytes(rows.Select(r => new
        {
            seq = r.Seq, occurred_at = DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc), actor = r.Actor, actor_address = r.ActorAddress,
            action = r.Action, entity_type = r.EntityType, entity_id = r.EntityId, accession = r.Accession, patient_id = r.PatientId,
            correlation_id = r.CorrelationId, reason = r.Reason,
            before = r.Before is null ? (JsonElement?)null : JsonDocument.Parse(r.Before).RootElement,
            after = r.After is null ? (JsonElement?)null : JsonDocument.Parse(r.After).RootElement,
            prev_hash = Convert.ToHexStringLower(r.PrevHash), hash = Convert.ToHexStringLower(r.Hash),
        }), new JsonSerializerOptions { WriteIndented = true });

    private static byte[] ToCsv(IReadOnlyList<AuditEventRow> rows)
    {
        var sb = new StringBuilder("seq,occurred_at_utc,actor,actor_address,action,entity_type,entity_id,accession,patient_id,correlation_id,reason,before_json,after_json,prev_hash,hash\r\n");
        foreach (var r in rows)
        {
            string[] cells =
            [
                r.Seq.ToString(CultureInfo.InvariantCulture),
                DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture),
                r.Actor, r.ActorAddress ?? "", r.Action, r.EntityType, r.EntityId ?? "", r.Accession ?? "", r.PatientId ?? "", r.CorrelationId ?? "",
                r.Reason ?? "", r.Before ?? "", r.After ?? "", Convert.ToHexStringLower(r.PrevHash), Convert.ToHexStringLower(r.Hash),
            ];
            sb.AppendJoin(',', cells.Select(Csv)).Append("\r\n");
        }
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    /// <summary>RFC 4180 quoting, and a leading quote mark against spreadsheet formula injection.</summary>
    private static string Csv(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
    }
}
