using Dapper;
using Lims.Audit;
using Lims.Platform;

namespace Lims.Instruments.Registration;

public sealed record AnalyzerDefinitionRow(Guid Id, string InstrumentId, int Version, string Model, string Protocol, bool Enabled, string Status,
    string ProposedBy, DateTime ProposedAt, string? ApprovedBy, DateTime? ApprovedAt, string? DisabledBy, DateTime? DisabledAt, string? DisableReason,
    string Definition)
{
    public AnalyzerDefinition Parsed => AnalyzerDefinition.FromJson(Definition);
}

/// <summary>
/// Registers analyzers that have no built-in adapter (design 6.1, 6.6). A Lab Manager proposes a definition; a
/// different Lab Manager approves it, and only then does the link start. A new approved version replaces the active
/// one and restarts the link. Disabling stops the link at once and needs a reason; enabling again is a new proposal.
/// Every step is audited. A registered analyzer starts as "unverified": nothing about it is interface-tested.
/// </summary>
public sealed class AnalyzerDefinitionService(Db db, AuditWriter audit, InstrumentDirectory directory)
{
    private const string Columns = """
        id, instrument_id AS InstrumentId, version, model, protocol, enabled, status, proposed_by AS ProposedBy, proposed_at AS ProposedAt,
        approved_by AS ApprovedBy, approved_at AS ApprovedAt, disabled_by AS DisabledBy, disabled_at AS DisabledAt, disable_reason AS DisableReason,
        definition::text AS Definition
        """;

    public async Task<IReadOnlyList<AnalyzerDefinitionRow>> ListAsync(bool history, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<AnalyzerDefinitionRow>($"""
            SELECT {Columns} FROM instruments.analyzer_definition
            WHERE @history OR status IN ('active','proposed') ORDER BY instrument_id, version DESC
            """, new { history })).ToList();
    }

    public async Task<AnalyzerDefinitionRow?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AnalyzerDefinitionRow>($"SELECT {Columns} FROM instruments.analyzer_definition WHERE id=@id", new { id });
    }

    /// <summary>Checks a definition against the other analyzers without saving it.</summary>
    public IReadOnlyList<string> Check(AnalyzerDefinition d) =>
        d.Validate(directory.All.Where(i => i.Enabled), directory.Configured.Select(c => c.Id));

    /// <summary>Proposes a new version. Replaces an earlier unapproved proposal for the same analyzer.</summary>
    public async Task<Guid> ProposeAsync(AnalyzerDefinition d, string user, CancellationToken ct, string? reason = null)
    {
        d.InstrumentId = d.InstrumentId.Trim().ToLowerInvariant();
        d.Model = d.Model.Trim();
        var errors = Check(d);
        if (errors.Count > 0) throw new WorkflowException(string.Join("; ", errors));
        var id = await db.InTransactionAsync(async (conn, tx) =>
        {
            // Serialise proposals for one analyzer so version numbers stay unique.
            await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('analyzer_definition:' || @InstrumentId))", d, tx);
            var replaced = await conn.ExecuteScalarAsync<Guid?>(
                "DELETE FROM instruments.analyzer_definition WHERE instrument_id=@InstrumentId AND status='proposed' RETURNING id", d, tx);
            var version = await conn.ExecuteScalarAsync<int>(
                "SELECT COALESCE(max(version),0)+1 FROM instruments.analyzer_definition WHERE instrument_id=@InstrumentId", d, tx);
            var newId = Guid.CreateVersion7();
            await conn.ExecuteAsync("""
                INSERT INTO instruments.analyzer_definition (id, instrument_id, version, model, protocol, definition, enabled, status, proposed_by)
                VALUES (@newId, @InstrumentId, @version, @Model, @Protocol, @json::jsonb, true, 'proposed', @user)
                """, new { newId, d.InstrumentId, version, d.Model, d.Protocol, json = d.ToJson(), user }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "analyzer.proposed", "analyzer_definition", $"{d.InstrumentId}:v{version}",
                After: d, Reason: reason ?? (replaced is null ? null : $"replaces unapproved proposal {replaced}")));
            return newId;
        }, ct);
        return id;
    }

    /// <summary>Approves a proposal (a different Lab Manager), makes it the active version and starts or restarts the link.</summary>
    public async Task ApproveAsync(Guid definitionId, string user, CancellationToken ct)
    {
        await db.InTransactionAsync(async (conn, tx) =>
        {
            var row = await conn.QuerySingleOrDefaultAsync<AnalyzerDefinitionRow>(
                $"SELECT {Columns} FROM instruments.analyzer_definition WHERE id=@definitionId FOR UPDATE", new { definitionId }, tx)
                ?? throw new WorkflowException("Analyzer definition not found");
            if (row.Status != "proposed") throw new WorkflowException("Only a proposed definition can be approved");
            if (row.ProposedBy == user) throw new WorkflowException("An analyzer must be approved by a different user than the one who proposed it");
            if (directory.IsConfigured(row.InstrumentId))
                throw new WorkflowException($"'{row.InstrumentId}' is now set up in the service configuration; this definition cannot be used");
            var d = row.Parsed;
            var errors = d.Validate(directory.All.Where(i => i.Enabled), directory.Configured.Select(c => c.Id));
            if (errors.Count > 0) throw new WorkflowException("The definition no longer fits: " + string.Join("; ", errors));

            var previous = await conn.QuerySingleOrDefaultAsync<int?>(
                "UPDATE instruments.analyzer_definition SET status='retired' WHERE instrument_id=@InstrumentId AND status='active' RETURNING version", row, tx);
            await conn.ExecuteAsync("""
                UPDATE instruments.analyzer_definition SET status='active', approved_by=@user, approved_at=now() WHERE id=@definitionId
                """, new { definitionId, user }, tx);
            // The instrument row carries the verification status shown on every screen. Any approved change to a verified
            // analyzer's definition means its verification no longer covers what runs.
            await conn.ExecuteAsync("""
                INSERT INTO instruments.instrument (id, model, protocol, software_version, verification_status)
                VALUES (@InstrumentId, @Model, @parser, @SoftwareVersion, 'unverified')
                ON CONFLICT (id) DO UPDATE SET model=EXCLUDED.model, protocol=EXCLUDED.protocol, software_version=EXCLUDED.software_version,
                    verification_status = CASE WHEN instruments.instrument.verification_status = 'verified' THEN 're-verify'
                                               ELSE instruments.instrument.verification_status END
                """, new { d.InstrumentId, d.Model, parser = d.ParserId, d.SoftwareVersion }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "analyzer.approved", "analyzer_definition", $"{row.InstrumentId}:v{row.Version}",
                Reason: previous is null ? "new analyzer" : $"replaces v{previous}"));
        }, ct);
        await ApplyAsync(ct);
    }

    /// <summary>Stops an analyzer's link at once. The definition stays; enabling it again is a new proposal that needs approval.</summary>
    public async Task DisableAsync(string instrumentId, string user, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new WorkflowException("A reason is required");
        await db.InTransactionAsync(async (conn, tx) =>
        {
            var version = await conn.QuerySingleOrDefaultAsync<int?>("""
                UPDATE instruments.analyzer_definition SET enabled=false, disabled_by=@user, disabled_at=now(), disable_reason=@reason
                WHERE instrument_id=@instrumentId AND status='active' AND enabled RETURNING version
                """, new { instrumentId, user, reason = reason.Trim() }, tx)
                ?? throw new WorkflowException("No enabled registered analyzer with that ID");
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "analyzer.disabled", "analyzer_definition", $"{instrumentId}:v{version}", Reason: reason.Trim()));
        }, ct);
        await ApplyAsync(ct);
    }

    /// <summary>
    /// Proposes an earlier (or the current, disabled) version again: used to roll back and to enable an analyzer
    /// again. A different Lab Manager must approve it.
    /// </summary>
    public async Task<Guid> ProposeCopyAsync(Guid definitionId, string user, CancellationToken ct)
    {
        var row = await GetAsync(definitionId, ct) ?? throw new WorkflowException("Analyzer definition not found");
        if (row is { Status: "active", Enabled: true }) throw new WorkflowException("That version is already active and enabled");
        if (row.Status == "proposed") throw new WorkflowException("That version is still waiting for approval");
        return await ProposeAsync(row.Parsed, user, ct, row.Status == "active" ? $"enable again (v{row.Version})" : $"restores v{row.Version}");
    }

    /// <summary>Withdraws a proposal that has not been approved.</summary>
    public Task WithdrawAsync(Guid definitionId, string user, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var row = await conn.QuerySingleOrDefaultAsync<(string InstrumentId, int Version)?>(
                "DELETE FROM instruments.analyzer_definition WHERE id=@definitionId AND status='proposed' RETURNING instrument_id, version", new { definitionId }, tx)
                ?? throw new WorkflowException("Only a proposal that is not yet approved can be withdrawn");
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "analyzer.proposal_withdrawn", "analyzer_definition", $"{row.InstrumentId}:v{row.Version}"));
        }, ct);

    private async Task ApplyAsync(CancellationToken ct)
    {
        await directory.RefreshAsync(ct);
        directory.NotifyChanged();
    }
}
