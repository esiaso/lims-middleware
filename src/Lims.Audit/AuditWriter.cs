using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Lims.Platform;
using Npgsql;

namespace Lims.Audit;

public sealed record AuditEntry(
    string Actor,
    string Action,
    string EntityType,
    string? EntityId,
    string? Accession = null,
    string? PatientId = null,
    string? CorrelationId = null,
    string? Reason = null,
    object? Before = null,
    object? After = null,
    string? ActorAddress = null);

public sealed record AuditRow(
    long Seq, DateTime OccurredAt, string Actor, string? ActorAddress, string Action, string EntityType,
    string? EntityId, string? Accession, string? PatientId, string? CorrelationId, string? Reason,
    string? BeforeJson, string? AfterJson, byte[] PrevHash, byte[] Hash);

/// <summary>
/// Appends hash-chained audit events inside the caller's transaction, so the audit record and the
/// state change commit or roll back together (design 9.6).
/// </summary>
public sealed class AuditWriter(IClock clock)
{
    private const long ChainLock = 0x4C494D53_000000A1;
    public static readonly byte[] GenesisHash = new byte[32];

    public async Task<long> WriteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, AuditEntry e)
    {
        // Serializes writers on the chain head; held until this transaction ends.
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(@k)", new { k = ChainLock }, tx);
        var head = await conn.QuerySingleOrDefaultAsync<(long Seq, byte[] Hash)?>(
            "SELECT seq, hash FROM audit.audit_event ORDER BY seq DESC LIMIT 1", transaction: tx);
        var seq = (head?.Seq ?? 0) + 1;
        var prev = head?.Hash ?? GenesisHash;

        // Normalize JSON through PostgreSQL so the hashed text equals what jsonb returns on verify.
        var before = await CanonicalJsonAsync(conn, tx, e.Before);
        var after = await CanonicalJsonAsync(conn, tx, e.After);
        var occurred = TruncateToMicros(clock.UtcNow.UtcDateTime);

        var row = new AuditRow(seq, occurred, e.Actor, e.ActorAddress, e.Action, e.EntityType, e.EntityId, e.Accession,
            e.PatientId, e.CorrelationId, e.Reason, before, after, prev, Array.Empty<byte>());
        var hash = ComputeHash(row);

        await conn.ExecuteAsync("""
            INSERT INTO audit.audit_event (seq, occurred_at, actor, actor_address, action, entity_type, entity_id,
                accession, patient_id, correlation_id, reason, before_json, after_json, prev_hash, hash)
            VALUES (@Seq, @OccurredAt, @Actor, @ActorAddress, @Action, @EntityType, @EntityId,
                @Accession, @PatientId, @CorrelationId, @Reason, @BeforeJson::jsonb, @AfterJson::jsonb, @PrevHash, @Hash)
            """, row with { Hash = hash }, tx);
        return seq;
    }

    public async Task<long> WriteAsync(Db db, AuditEntry e, CancellationToken ct) =>
        await db.InTransactionAsync((c, t) => WriteAsync(c, t, e), ct);

    private static async Task<string?> CanonicalJsonAsync(NpgsqlConnection conn, NpgsqlTransaction tx, object? value)
    {
        if (value is null) return null;
        var json = value as string ?? Json.Serialize(value);
        return await conn.ExecuteScalarAsync<string>("SELECT (@j::jsonb)::text", new { j = json }, tx);
    }

    private static DateTime TruncateToMicros(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);

    public static byte[] ComputeHash(AuditRow r)
    {
        var sb = new StringBuilder();
        void Add(string? s) { sb.Append(s is null ? "␀" : s.Replace("\u001F", "", StringComparison.Ordinal)); sb.Append('\u001F'); }
        Add(r.Seq.ToString(CultureInfo.InvariantCulture));
        Add(DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture));
        Add(r.Actor); Add(r.ActorAddress); Add(r.Action); Add(r.EntityType); Add(r.EntityId);
        Add(r.Accession); Add(r.PatientId); Add(r.CorrelationId); Add(r.Reason); Add(r.BeforeJson); Add(r.AfterJson);
        var content = Encoding.UTF8.GetBytes(sb.ToString());
        var buf = new byte[r.PrevHash.Length + content.Length];
        r.PrevHash.CopyTo(buf, 0);
        content.CopyTo(buf, r.PrevHash.Length);
        return SHA256.HashData(buf);
    }
}
