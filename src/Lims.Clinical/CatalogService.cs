using Dapper;
using Lims.Audit;
using Lims.Platform;

namespace Lims.Clinical;

public sealed record CatalogEntry(string Code, string Name, string? Loinc, string? UcumUnit, int Decimals, decimal? RefLow, decimal? RefHigh,
    decimal? CriticalLow, decimal? CriticalHigh, string SpecimenType, string? DefaultInstrument,
    decimal? DeltaPercent = null, decimal? DeltaAbsolute = null, int DeltaWindowDays = 30,
    IReadOnlyList<string>? PanelComponents = null)
{
    public bool IsPanel => PanelComponents is { Count: > 0 };
}

/// <summary>Versioned test catalog (LOINC only; SHA coding stays in the HMIS, decision E9).</summary>
public sealed class CatalogService(Db db, AuditWriter audit)
{
    public Task<int> PublishAsync(CatalogEntry e, string user, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var components = await ValidatePanelAsync(conn, tx, e);
            var version = await conn.ExecuteScalarAsync<int>("SELECT COALESCE(max(version),0)+1 FROM clinical.test_catalog WHERE code=@Code", e, tx);
            await conn.ExecuteAsync("UPDATE clinical.test_catalog SET status='retired' WHERE code=@Code AND status='active'", e, tx);
            await conn.ExecuteAsync("""
                INSERT INTO clinical.test_catalog (code, version, name, loinc, ucum_unit, decimals, ref_low, ref_high, critical_low, critical_high,
                    specimen_type, default_instrument, delta_percent, delta_absolute, delta_window_days, panel_components, status, approved_by)
                VALUES (@Code, @version, @Name, @Loinc, @UcumUnit, @Decimals, @RefLow, @RefHigh, @CriticalLow, @CriticalHigh,
                    @SpecimenType, @DefaultInstrument, @DeltaPercent, @DeltaAbsolute, @DeltaWindowDays, @components, 'active', @user)
                """, new { e.Code, version, e.Name, e.Loinc, e.UcumUnit, e.Decimals, e.RefLow, e.RefHigh, e.CriticalLow, e.CriticalHigh, e.SpecimenType, e.DefaultInstrument,
                    e.DeltaPercent, e.DeltaAbsolute, e.DeltaWindowDays, components, user }, tx);
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
                    specimen_type, default_instrument, tat_minutes, delta_percent, delta_absolute, delta_window_days, panel_components, status, source, proposed_by)
                SELECT @code, @version, @name, @loinc, @unit,
                       COALESCE(a.decimals, 2), a.ref_low, a.ref_high, a.critical_low, a.critical_high,
                       COALESCE(a.specimen_type, 'serum'), a.default_instrument, COALESCE(a.tat_minutes, 240),
                       a.delta_percent, a.delta_absolute, COALESCE(a.delta_window_days, 30), a.panel_components, 'proposed', 'hmis', 'system:catalog-sync'
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
            var components = await ValidatePanelAsync(conn, tx, e);
            await conn.ExecuteAsync("DELETE FROM clinical.test_catalog WHERE code=@Code AND status='proposed'", e, tx);
            var version = await conn.ExecuteScalarAsync<int>("SELECT COALESCE(max(version),0)+1 FROM clinical.test_catalog WHERE code=@Code", e, tx);
            await conn.ExecuteAsync("""
                INSERT INTO clinical.test_catalog (code, version, name, loinc, ucum_unit, decimals, ref_low, ref_high, critical_low, critical_high,
                    specimen_type, default_instrument, delta_percent, delta_absolute, delta_window_days, panel_components, tat_minutes, status, source, proposed_by)
                SELECT @Code, @version, @Name, @Loinc, @UcumUnit, @Decimals, @RefLow, @RefHigh, @CriticalLow, @CriticalHigh,
                    @SpecimenType, @DefaultInstrument, @DeltaPercent, @DeltaAbsolute, @DeltaWindowDays, @components,
                    COALESCE((SELECT tat_minutes FROM clinical.test_catalog WHERE code=@Code AND status='active'), 240), 'proposed', 'manual', @user
                """, new { e.Code, version, e.Name, e.Loinc, e.UcumUnit, e.Decimals, e.RefLow, e.RefHigh, e.CriticalLow, e.CriticalHigh, e.SpecimenType,
                    e.DefaultInstrument, e.DeltaPercent, e.DeltaAbsolute, e.DeltaWindowDays, components, user }, tx);
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

    /// <summary>
    /// Roll back (design 8): proposes the content of an earlier version as a new version. Like any change it takes effect only
    /// when a different Lab Manager approves it, so a rollback cannot bypass four-eyes review.
    /// </summary>
    public async Task<int> ProposeRollbackAsync(string code, int version, string user, CancellationToken ct)
    {
        CatalogEntry entry;
        await using (var conn = await db.OpenAsync(ct))
        {
            var row = await conn.QuerySingleOrDefaultAsync<CatalogVersionRow>("""
                SELECT code, name, loinc, ucum_unit AS UcumUnit, decimals, ref_low AS RefLow, ref_high AS RefHigh, critical_low AS CriticalLow,
                       critical_high AS CriticalHigh, specimen_type AS SpecimenType, default_instrument AS DefaultInstrument,
                       delta_percent AS DeltaPercent, delta_absolute AS DeltaAbsolute, delta_window_days AS DeltaWindowDays,
                       panel_components AS PanelComponents, status
                FROM clinical.test_catalog WHERE code=@code AND version=@version
                """, new { code, version }) ?? throw new WorkflowException("Catalog version not found");
            if (row.Status == "active") throw new WorkflowException("That version is already active");
            entry = new CatalogEntry(row.Code, row.Name, row.Loinc, row.UcumUnit, row.Decimals, row.RefLow, row.RefHigh, row.CriticalLow, row.CriticalHigh,
                row.SpecimenType, row.DefaultInstrument, row.DeltaPercent, row.DeltaAbsolute, row.DeltaWindowDays, row.PanelComponents);
        }
        var v = await ProposeAsync(entry, user, ct);
        await audit.WriteAsync(db, new AuditEntry(user, "catalog.rollback_proposed", "test_catalog", $"{code}:v{v}", Reason: $"restores v{version}"), ct);
        return v;
    }

    private sealed class CatalogVersionRow
    {
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Loinc { get; init; }
        public string? UcumUnit { get; init; }
        public int Decimals { get; init; }
        public decimal? RefLow { get; init; }
        public decimal? RefHigh { get; init; }
        public decimal? CriticalLow { get; init; }
        public decimal? CriticalHigh { get; init; }
        public string SpecimenType { get; init; } = "serum";
        public string? DefaultInstrument { get; init; }
        public decimal? DeltaPercent { get; init; }
        public decimal? DeltaAbsolute { get; init; }
        public int DeltaWindowDays { get; init; }
        public string[]? PanelComponents { get; init; }
        public string Status { get; init; } = "";
    }

    /// <summary>
    /// A panel lists active, non-panel catalog tests, each once. Returns the component array to store (null for a plain test).
    /// </summary>
    private static async Task<string[]?> ValidatePanelAsync(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction tx, CatalogEntry e)
    {
        if (!e.IsPanel) return null;
        var components = e.PanelComponents!.Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
        if (components.Length == 0) return null;
        if (components.Distinct().Count() != components.Length) throw new WorkflowException("A panel lists a component more than once");
        if (components.Contains(e.Code)) throw new WorkflowException("A panel cannot contain itself");
        var found = (await conn.QueryAsync<(string Code, bool IsPanel)>(
            "SELECT code, panel_components IS NOT NULL FROM clinical.test_catalog WHERE code = ANY(@components) AND status='active'",
            new { components }, tx)).ToList();
        var missing = components.Except(found.Select(f => f.Code)).ToList();
        if (missing.Count > 0) throw new WorkflowException($"Panel components are not active catalog tests: {string.Join(", ", missing)}");
        if (found.Any(f => f.IsPanel)) throw new WorkflowException("A panel cannot contain another panel");
        return components;
    }

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
