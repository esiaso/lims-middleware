using Dapper;
using Lims.Audit;
using Lims.Platform;

namespace Lims.Instruments;

public sealed record MappingProposal(string InstrumentId, string InstrumentCode, string CatalogCode, string ResultType, decimal UnitFactor, string? InstrumentUnit, bool Reportable);

/// <summary>Versioned instrument-code mappings: propose, then approve by a different Lab Manager (design 6.6).</summary>
public sealed class MappingService(Db db, AuditWriter audit)
{
    public Task<Guid> ProposeAsync(MappingProposal p, string user, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var version = await conn.ExecuteScalarAsync<int>(
                "SELECT COALESCE(max(version),0)+1 FROM instruments.test_mapping WHERE instrument_id=@InstrumentId AND instrument_code=@InstrumentCode", p, tx);
            var id = Guid.CreateVersion7();
            await conn.ExecuteAsync("""
                INSERT INTO instruments.test_mapping (id, instrument_id, instrument_code, catalog_code, version, result_type, unit_factor,
                    instrument_unit, reportable, status, proposed_by)
                VALUES (@id, @InstrumentId, @InstrumentCode, @CatalogCode, @version, @ResultType, @UnitFactor, @InstrumentUnit, @Reportable, 'proposed', @user)
                """, new { id, p.InstrumentId, p.InstrumentCode, p.CatalogCode, version, p.ResultType, p.UnitFactor, p.InstrumentUnit, p.Reportable, user }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "mapping.proposed", "test_mapping", id.ToString(), After: p));
            return id;
        }, ct);

    public Task ApproveAsync(Guid mappingId, string user, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var m = await conn.QuerySingleOrDefaultAsync<(string InstrumentId, string Code, string Status, string ProposedBy)?>(
                "SELECT instrument_id, instrument_code, status, proposed_by FROM instruments.test_mapping WHERE id=@mappingId FOR UPDATE", new { mappingId }, tx)
                ?? throw new WorkflowException("Mapping not found");
            if (m.Status != "proposed") throw new WorkflowException("Only a proposed mapping can be approved");
            if (m.ProposedBy == user) throw new WorkflowException("A mapping must be approved by a different user than the one who proposed it");
            await conn.ExecuteAsync("UPDATE instruments.test_mapping SET status='retired' WHERE instrument_id=@i AND instrument_code=@c AND status='active'",
                new { i = m.InstrumentId, c = m.Code }, tx);
            await conn.ExecuteAsync("UPDATE instruments.test_mapping SET status='active', approved_by=@user, approved_at=now() WHERE id=@mappingId",
                new { mappingId, user }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "mapping.approved", "test_mapping", mappingId.ToString()));
        }, ct);
}
