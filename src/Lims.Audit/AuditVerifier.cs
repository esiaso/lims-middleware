using Dapper;
using Lims.Platform;

namespace Lims.Audit;

public sealed record ChainVerification(bool Valid, long RowsChecked, long? FirstBadSeq, string? Problem);

/// <summary>Recomputes the whole chain and checks external anchors (design 9.6 "verify chain").</summary>
public sealed class AuditVerifier(Db db)
{
    public async Task<ChainVerification> VerifyAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = conn.Query<AuditRow>("""
            SELECT seq, occurred_at AS OccurredAt, actor, actor_address AS ActorAddress, action, entity_type AS EntityType,
                   entity_id AS EntityId, accession, patient_id AS PatientId, correlation_id AS CorrelationId, reason,
                   before_json::text AS BeforeJson, after_json::text AS AfterJson, prev_hash AS PrevHash, hash
            FROM audit.audit_event ORDER BY seq
            """, buffered: false);

        var expectedPrev = AuditWriter.GenesisHash;
        long expectedSeq = 1, count = 0;
        var hashes = new Dictionary<long, byte[]>();
        foreach (var r in rows)
        {
            count++;
            if (r.Seq != expectedSeq) return new(false, count, expectedSeq, $"Missing or reordered row: expected seq {expectedSeq}, found {r.Seq}");
            if (!r.PrevHash.AsSpan().SequenceEqual(expectedPrev)) return new(false, count, r.Seq, "prev_hash does not match previous row");
            var recomputed = AuditWriter.ComputeHash(r);
            if (!recomputed.AsSpan().SequenceEqual(r.Hash)) return new(false, count, r.Seq, "Row content does not match its hash");
            hashes[r.Seq] = r.Hash;
            expectedPrev = r.Hash;
            expectedSeq++;
        }

        var anchors = await conn.QueryAsync<(long Seq, byte[] Hash)>("SELECT seq, hash FROM audit.chain_anchor ORDER BY id");
        foreach (var a in anchors)
        {
            if (!hashes.TryGetValue(a.Seq, out var h) || !h.AsSpan().SequenceEqual(a.Hash))
                return new(false, count, a.Seq, "Chain does not match a recorded anchor");
        }
        return new(true, count, null, null);
    }
}
