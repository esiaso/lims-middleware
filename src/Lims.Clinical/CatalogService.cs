using Dapper;
using Lims.Audit;
using Lims.Platform;

namespace Lims.Clinical;

public sealed record CatalogEntry(string Code, string Name, string? Loinc, string? UcumUnit, int Decimals, decimal? RefLow, decimal? RefHigh,
    decimal? CriticalLow, decimal? CriticalHigh, string SpecimenType, string? DefaultInstrument);

/// <summary>Versioned test catalog (LOINC only; SHA coding stays in the HMIS, decision E9).</summary>
public sealed class CatalogService(Db db, AuditWriter audit)
{
    public Task<int> PublishAsync(CatalogEntry e, string user, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var version = await conn.ExecuteScalarAsync<int>("SELECT COALESCE(max(version),0)+1 FROM clinical.test_catalog WHERE code=@Code", e, tx);
            await conn.ExecuteAsync("UPDATE clinical.test_catalog SET status='retired' WHERE code=@Code AND status='active'", e, tx);
            await conn.ExecuteAsync("""
                INSERT INTO clinical.test_catalog (code, version, name, loinc, ucum_unit, decimals, ref_low, ref_high, critical_low, critical_high,
                    specimen_type, default_instrument, status, approved_by)
                VALUES (@Code, @version, @Name, @Loinc, @UcumUnit, @Decimals, @RefLow, @RefHigh, @CriticalLow, @CriticalHigh,
                    @SpecimenType, @DefaultInstrument, 'active', @user)
                """, new { e.Code, version, e.Name, e.Loinc, e.UcumUnit, e.Decimals, e.RefLow, e.RefHigh, e.CriticalLow, e.CriticalHigh, e.SpecimenType, e.DefaultInstrument, user }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "catalog.published", "test_catalog", $"{e.Code}:v{version}", After: e));
            return version;
        }, ct);
}
