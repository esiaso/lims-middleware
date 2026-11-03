using Dapper;
using Lims.Audit;
using Lims.Platform;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lims.Clinical;

public sealed class AutoverificationOptions
{
    /// <summary>Emergency stop: true ignores every active rule (all results go to a person) without changing the rules.</summary>
    public bool Suspended { get; set; }
}

public sealed record AutoverifyRuleProposal(string CatalogCode, string? InstrumentId, decimal? LimitLow, decimal? LimitHigh, int QcWindowHours,
    string ValidationDocumentId);

/// <summary>
/// Autoverification (design 5.6). Off at install: nothing is autoverified until a Lab Manager proposes a rule for a test,
/// citing the validation study document, and a different Lab Manager approves it. A result is released by a rule only when
/// every condition holds; otherwise it waits for a person as usual and the reasons are audited.
/// <list type="number">
/// <item>First run of the test on this specimen (reruns and corrections always go to a person).</item>
/// <item>Numeric, no comparator ("&lt;", "&gt;"), not critical, no flag mismatch, delta check passed.</item>
/// <item>No instrument or abnormal flags other than "N".</item>
/// <item>Value inside the rule limits, or the catalog reference range when the rule has none.</item>
/// <item>QC for the same instrument and test within the rule's window, and the latest QC has no flags other than "N".
/// The middleware does not evaluate Westgard rules; this checks only that QC ran and the analyzer did not flag it.</item>
/// </list>
/// </summary>
public sealed class AutoverificationService(Db db, AuditWriter audit, ResultReviewService review, IOptions<AutoverificationOptions> options)
{
    public Task<Guid> ProposeAsync(AutoverifyRuleProposal p, string user, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(p.ValidationDocumentId)) throw new WorkflowException("The validation study document ID is required");
            if (p.LimitLow > p.LimitHigh) throw new WorkflowException("Low limit is above high limit");
            if (p.QcWindowHours is < 1 or > 168) throw new WorkflowException("QC window must be 1 to 168 hours");
            var catalog = await conn.QuerySingleOrDefaultAsync<(decimal? RefLow, decimal? RefHigh, bool IsPanel)?>(
                "SELECT ref_low, ref_high, panel_components IS NOT NULL FROM clinical.test_catalog WHERE code=@CatalogCode AND status='active'", p, tx)
                ?? throw new WorkflowException("Not an active catalog test");
            if (catalog.IsPanel) throw new WorkflowException("Rules apply to the tests in a panel, not to the panel");
            if (p.LimitLow is null && p.LimitHigh is null && (catalog.RefLow is null || catalog.RefHigh is null))
                throw new WorkflowException("The test has no reference range: give the rule its own limits");
            await conn.ExecuteAsync("DELETE FROM clinical.autoverify_rule WHERE catalog_code=@CatalogCode AND status='proposed'", p, tx);
            var version = await conn.ExecuteScalarAsync<int>("SELECT COALESCE(max(version),0)+1 FROM clinical.autoverify_rule WHERE catalog_code=@CatalogCode", p, tx);
            var id = Guid.CreateVersion7();
            await conn.ExecuteAsync("""
                INSERT INTO clinical.autoverify_rule (id, catalog_code, version, instrument_id, limit_low, limit_high, qc_window_hours,
                    validation_document_id, status, proposed_by)
                VALUES (@id, @CatalogCode, @version, @InstrumentId, @LimitLow, @LimitHigh, @QcWindowHours, @doc, 'proposed', @user)
                """, new { id, p.CatalogCode, version, p.InstrumentId, p.LimitLow, p.LimitHigh, p.QcWindowHours, doc = p.ValidationDocumentId.Trim(), user }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "autoverify.proposed", "autoverify_rule", id.ToString(), After: p with { }));
            return id;
        }, ct);

    public Task ApproveAsync(Guid ruleId, string user, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var r = await conn.QuerySingleOrDefaultAsync<(string Code, string Status, string ProposedBy)?>(
                "SELECT catalog_code, status, proposed_by FROM clinical.autoverify_rule WHERE id=@ruleId FOR UPDATE", new { ruleId }, tx)
                ?? throw new WorkflowException("Rule not found");
            if (r.Status != "proposed") throw new WorkflowException("Only a proposed rule can be approved");
            if (r.ProposedBy == user) throw new WorkflowException("An autoverification rule must be approved by a different user than the one who proposed it");
            await conn.ExecuteAsync("""
                UPDATE clinical.autoverify_rule SET status='retired', retired_by=@user, retired_at=now(), retire_reason='replaced by a new version'
                WHERE catalog_code=@code AND status='active'
                """, new { code = r.Code, user }, tx);
            await conn.ExecuteAsync("UPDATE clinical.autoverify_rule SET status='active', approved_by=@user, approved_at=now() WHERE id=@ruleId", new { ruleId, user }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "autoverify.enabled", "autoverify_rule", ruleId.ToString(), After: new { r.Code }));
        }, ct);

    /// <summary>Turns a rule off at once (no second approval needed to make the system more conservative).</summary>
    public Task DisableAsync(Guid ruleId, string user, string reason, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new WorkflowException("A reason is required");
            var n = await conn.ExecuteAsync("""
                UPDATE clinical.autoverify_rule SET status='retired', retired_by=@user, retired_at=now(), retire_reason=@reason
                WHERE id=@ruleId AND status IN ('active','proposed')
                """, new { ruleId, user, reason }, tx);
            if (n == 0) throw new WorkflowException("Rule is not active or proposed");
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "autoverify.disabled", "autoverify_rule", ruleId.ToString(), Reason: reason));
        }, ct);

    private sealed class Candidate
    {
        public int Version { get; init; }
        public string ValueType { get; init; } = "";
        public string? Comparator { get; init; }
        public decimal? NumericValue { get; init; }
        public bool IsCritical { get; init; }
        public bool FlagMismatch { get; init; }
        public bool DeltaFlag { get; init; }
        public string? AbnormalFlag { get; init; }
        public string[] InstrumentFlags { get; init; } = [];
        public string CatalogCode { get; init; } = "";
        public decimal? RefLow { get; init; }
        public decimal? RefHigh { get; init; }
        public string? InstrumentId { get; init; }
        public string? InstrumentTestCode { get; init; }
        public string Accession { get; init; } = "";
        public string Mrn { get; init; } = "";
    }

    private sealed class Rule
    {
        public Guid Id { get; init; }
        public int Version { get; init; }
        public string? InstrumentId { get; init; }
        public decimal? LimitLow { get; init; }
        public decimal? LimitHigh { get; init; }
        public int QcWindowHours { get; init; }
    }

    /// <summary>Called in the inbox transaction right after a result is stored. Returns true when the rule released it.</summary>
    public async Task<bool> TryAutoverifyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid resultId)
    {
        if (options.Value.Suspended) return false;
        var c = await conn.QuerySingleAsync<Candidate>("""
            SELECT r.version, r.value_type AS ValueType, r.comparator, r.numeric_value AS NumericValue, r.is_critical AS IsCritical,
                   r.flag_mismatch AS FlagMismatch, r.delta_flag AS DeltaFlag, r.abnormal_flag AS AbnormalFlag, r.instrument_flags AS InstrumentFlags,
                   ot.catalog_code AS CatalogCode, cat.ref_low AS RefLow, cat.ref_high AS RefHigh,
                   ir.instrument_id AS InstrumentId, ir.instrument_test_code AS InstrumentTestCode, s.accession, p.mrn
            FROM clinical.result r
            JOIN clinical.specimen_test st ON st.id = r.specimen_test_id
            JOIN clinical.specimen s ON s.id = st.specimen_id
            JOIN clinical.order_test ot ON ot.id = st.order_test_id
            JOIN clinical.lab_order o ON o.id = ot.order_id
            JOIN clinical.patient p ON p.id = o.patient_id
            LEFT JOIN clinical.test_catalog cat ON cat.code = ot.catalog_code AND cat.status = 'active'
            LEFT JOIN clinical.instrument_result ir ON ir.id = r.instrument_result_id
            WHERE r.id = @resultId
            """, new { resultId }, tx);
        var rule = await conn.QuerySingleOrDefaultAsync<Rule>("""
            SELECT id, version, instrument_id AS InstrumentId, limit_low AS LimitLow, limit_high AS LimitHigh, qc_window_hours AS QcWindowHours
            FROM clinical.autoverify_rule WHERE catalog_code=@CatalogCode AND status='active'
            """, new { c.CatalogCode }, tx);
        if (rule is null) return false;

        var reasons = new List<string>();
        if (rule.InstrumentId is not null && rule.InstrumentId != c.InstrumentId) reasons.Add("instrument not covered by the rule");
        if (c.Version != 1) reasons.Add("not the first run");
        if (c.ValueType != "numeric" || c.NumericValue is null) reasons.Add("not a numeric value");
        if (c.Comparator is not null) reasons.Add("value has a comparator");
        if (c.IsCritical) reasons.Add("critical value");
        if (c.FlagMismatch) reasons.Add("instrument flag differs from range check");
        if (c.DeltaFlag) reasons.Add("delta check failed");
        if (c.AbnormalFlag is not (null or "N")) reasons.Add($"abnormal flag {c.AbnormalFlag}");
        if (c.InstrumentFlags.Any(f => f != "N")) reasons.Add($"instrument flags {string.Join(" ", c.InstrumentFlags.Where(f => f != "N"))}");
        var low = rule.LimitLow ?? (rule.LimitHigh is null ? c.RefLow : null);
        var high = rule.LimitHigh ?? (rule.LimitLow is null ? c.RefHigh : null);
        if (c.NumericValue is { } v && ((low is { } lo && v < lo) || (high is { } hi && v > hi))) reasons.Add("outside autoverification limits");
        if (low is null && high is null) reasons.Add("no limits");

        if (c.InstrumentId is null || c.InstrumentTestCode is null) reasons.Add("no instrument run");
        else
        {
            var qc = await conn.QuerySingleOrDefaultAsync<string[]?>("""
                SELECT flags FROM clinical.qc_result
                WHERE instrument_id=@inst AND instrument_test_code=@code AND received_at >= now() - make_interval(hours => @hours)
                ORDER BY received_at DESC LIMIT 1
                """, new { inst = c.InstrumentId, code = c.InstrumentTestCode, hours = rule.QcWindowHours }, tx);
            if (qc is null) reasons.Add($"no QC in the last {rule.QcWindowHours} h");
            else if (qc.Any(f => f != "N")) reasons.Add("latest QC is flagged");
        }

        if (reasons.Count > 0)
        {
            await audit.WriteAsync(conn, tx, new AuditEntry($"autoverify:{c.CatalogCode}:v{rule.Version}", "result.autoverify_skipped", "result", resultId.ToString(),
                Accession: c.Accession, PatientId: c.Mrn, Reason: string.Join("; ", reasons)));
            return false;
        }
        try
        {
            await review.ApproveInTransactionAsync(conn, tx, resultId, $"autoverify:{c.CatalogCode}:v{rule.Version}", null, null, rule.Id);
            return true;
        }
        catch (WorkflowException ex)
        {
            // Approval rules are checked before anything is written, so the result simply stays for a person.
            await audit.WriteAsync(conn, tx, new AuditEntry($"autoverify:{c.CatalogCode}:v{rule.Version}", "result.autoverify_skipped", "result", resultId.ToString(),
                Accession: c.Accession, PatientId: c.Mrn, Reason: ex.Message));
            return false;
        }
    }
}
