using Dapper;
using Lims.Audit;
using Lims.Platform;

namespace Lims.Clinical;

public sealed record CatalogEntry(string Code, string Name, string? Loinc, string? UcumUnit, int Decimals, decimal? RefLow, decimal? RefHigh,
    decimal? CriticalLow, decimal? CriticalHigh, string SpecimenType, string? DefaultInstrument,
    decimal? DeltaPercent = null, decimal? DeltaAbsolute = null, int DeltaWindowDays = 30);

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
                    specimen_type, default_instrument, delta_percent, delta_absolute, delta_window_days, status, approved_by)
                VALUES (@Code, @version, @Name, @Loinc, @UcumUnit, @Decimals, @RefLow, @RefHigh, @CriticalLow, @CriticalHigh,
                    @SpecimenType, @DefaultInstrument, @DeltaPercent, @DeltaAbsolute, @DeltaWindowDays, 'active', @user)
                """, new { e.Code, version, e.Name, e.Loinc, e.UcumUnit, e.Decimals, e.RefLow, e.RefHigh, e.CriticalLow, e.CriticalHigh, e.SpecimenType, e.DefaultInstrument,
                    e.DeltaPercent, e.DeltaAbsolute, e.DeltaWindowDays, user }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "catalog.published", "test_catalog", $"{e.Code}:v{version}", After: e));
            return version;
        }, ct);

    /// <summary>
    /// Records a catalog change seen in the HMIS as a PROPOSED version (design 6.9: daily sync goes to the approval
    /// queue). Ranges and limits are carried from the active version; nothing changes until a Lab Manager approves.
    /// Returns false when the HMIS entry already matches the active or proposed version.
    /// </summary>
    public Task<bool> ProposeFromHmisAsync(string code, string name, string? loinc, string? unit, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var current = await conn.QuerySingleOrDefaultAsync<CatalogEntryRow>("""
                SELECT code, version, name, loinc, ucum_unit AS UcumUnit, status FROM clinical.test_catalog
                WHERE code=@code AND status IN ('active','proposed') ORDER BY (status='proposed') DESC, version DESC LIMIT 1
                """, new { code }, tx);
            if (current is not null && current.Name == name && current.Loinc == loinc && current.UcumUnit == unit) return false;
            await conn.ExecuteAsync("DELETE FROM clinical.test_catalog WHERE code=@code AND status='proposed'", new { code }, tx);
            var version = await conn.ExecuteScalarAsync<int>("SELECT COALESCE(max(version),0)+1 FROM clinical.test_catalog WHERE code=@code", new { code }, tx);
            await conn.ExecuteAsync("""
                INSERT INTO clinical.test_catalog (code, version, name, loinc, ucum_unit, decimals, ref_low, ref_high, critical_low, critical_high,
                    specimen_type, default_instrument, tat_minutes, delta_percent, delta_absolute, delta_window_days, status, source, proposed_by)
                SELECT @code, @version, @name, @loinc, @unit,
                       COALESCE(a.decimals, 2), a.ref_low, a.ref_high, a.critical_low, a.critical_high,
                       COALESCE(a.specimen_type, 'serum'), a.default_instrument, COALESCE(a.tat_minutes, 240),
                       a.delta_percent, a.delta_absolute, COALESCE(a.delta_window_days, 30), 'proposed', 'hmis', 'system:catalog-sync'
                FROM (SELECT 1) one LEFT JOIN clinical.test_catalog a ON a.code=@code AND a.status='active'
                """, new { code, version, name, loinc, unit }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry("system:catalog-sync", "catalog.proposed", "test_catalog", $"{code}:v{version}",
                Before: current is null ? null : new { current.Name, current.Loinc, current.UcumUnit }, After: new { name, loinc, unit }));
            return true;
        }, ct);

    /// <summary>
    /// A Lab Manager proposes a new catalog version (ranges, critical limits, delta limits). It takes effect only when a
    /// different Lab Manager approves it. Replaces any earlier unapproved proposal for the same test.
    /// </summary>
    public Task<int> ProposeAsync(CatalogEntry e, string user, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(e.Code) || string.IsNullOrWhiteSpace(e.Name)) throw new WorkflowException("Code and name are required");
            if (e.RefLow > e.RefHigh || e.CriticalLow > e.CriticalHigh) throw new WorkflowException("Low limit is above high limit");
            await conn.ExecuteAsync("DELETE FROM clinical.test_catalog WHERE code=@Code AND status='proposed'", e, tx);
            var version = await conn.ExecuteScalarAsync<int>("SELECT COALESCE(max(version),0)+1 FROM clinical.test_catalog WHERE code=@Code", e, tx);
            await conn.ExecuteAsync("""
                INSERT INTO clinical.test_catalog (code, version, name, loinc, ucum_unit, decimals, ref_low, ref_high, critical_low, critical_high,
                    specimen_type, default_instrument, delta_percent, delta_absolute, delta_window_days, tat_minutes, status, source, proposed_by)
                SELECT @Code, @version, @Name, @Loinc, @UcumUnit, @Decimals, @RefLow, @RefHigh, @CriticalLow, @CriticalHigh,
                    @SpecimenType, @DefaultInstrument, @DeltaPercent, @DeltaAbsolute, @DeltaWindowDays,
                    COALESCE((SELECT tat_minutes FROM clinical.test_catalog WHERE code=@Code AND status='active'), 240), 'proposed', 'manual', @user
                """, new { e.Code, version, e.Name, e.Loinc, e.UcumUnit, e.Decimals, e.RefLow, e.RefHigh, e.CriticalLow, e.CriticalHigh, e.SpecimenType,
                    e.DefaultInstrument, e.DeltaPercent, e.DeltaAbsolute, e.DeltaWindowDays, user }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "catalog.proposed", "test_catalog", $"{e.Code}:v{version}", After: e));
            return version;
        }, ct);

    /// <summary>Lab Manager approves a proposed catalog version; the previous active version is retired.</summary>
    public Task ApproveAsync(string code, string user, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var proposedBy = await conn.ExecuteScalarAsync<string?>(
                "SELECT proposed_by FROM clinical.test_catalog WHERE code=@code AND status='proposed' FOR UPDATE", new { code }, tx)
                ?? throw new WorkflowException("No proposed version for this test");
            if (proposedBy == user) throw new WorkflowException("A catalog change must be approved by a different user than the one who proposed it");
            await conn.ExecuteAsync("UPDATE clinical.test_catalog SET status='retired' WHERE code=@code AND status='active'", new { code }, tx);
            var version = await conn.ExecuteScalarAsync<int>(
                "UPDATE clinical.test_catalog SET status='active', approved_by=@user WHERE code=@code AND status='proposed' RETURNING version", new { code, user }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "catalog.approved", "test_catalog", $"{code}:v{version}"));
        }, ct);

    private sealed class CatalogEntryRow
    {
        public string Code { get; init; } = "";
        public int Version { get; init; }
        public string Name { get; init; } = "";
        public string? Loinc { get; init; }
        public string? UcumUnit { get; init; }
        public string Status { get; init; } = "";
    }
}
