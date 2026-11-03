using Dapper;
using Lims.Clinical;
using Lims.Instruments;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class ConfigurationModel(Db db, CatalogService catalog, MappingService mappings, AutoverificationService autoverify,
    Microsoft.Extensions.Options.IOptions<AutoverificationOptions> autoverifyOptions) : PageModel
{
    public IReadOnlyList<dynamic> Catalog { get; private set; } = [];
    public IReadOnlyList<dynamic> Mappings { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public bool History { get; set; }
    public IReadOnlyList<dynamic> Rules { get; private set; } = [];
    public bool AutoverifySuspended => autoverifyOptions.Value.Suspended;
    public string? Message { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        // With history on, retired versions are listed too so a Lab Manager can compare and roll back.
        Catalog = (await conn.QueryAsync("SELECT * FROM clinical.test_catalog WHERE @History OR status IN ('active','proposed') ORDER BY code, version DESC", new { History })).ToList();
        Mappings = (await conn.QueryAsync("SELECT * FROM instruments.test_mapping WHERE @History OR status IN ('active','proposed') ORDER BY instrument_id, instrument_code, version DESC", new { History })).ToList();
        Rules = (await conn.QueryAsync("""
            SELECT r.*, (SELECT count(*) FROM clinical.review v WHERE v.autoverify_rule_id = r.id) AS released
            FROM clinical.autoverify_rule r WHERE @History OR r.status IN ('active','proposed') ORDER BY r.catalog_code, r.version DESC
            """, new { History })).ToList();
    }

    private async Task<IActionResult> Run(Func<Task> action, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.LabManager)) return Forbid();
        try { await action(); return RedirectToPage(); }
        catch (WorkflowException ex) { Message = ex.Message; await LoadAsync(ct); return Page(); }
    }

    public Task<IActionResult> OnPostApproveCatalogAsync(string code, CancellationToken ct) => Run(() => catalog.ApproveAsync(code, User.Identity!.Name!, ct), ct);
    public Task<IActionResult> OnPostProposeCatalogAsync(string code, string name, string? loinc, string? unit, int decimals,
        decimal? refLow, decimal? refHigh, decimal? criticalLow, decimal? criticalHigh, string specimenType, string? instrument,
        decimal? deltaPercent, decimal? deltaAbsolute, int deltaWindowDays, string? panelComponents, CancellationToken ct) =>
        Run(() => catalog.ProposeAsync(new CatalogEntry(code.Trim().ToUpperInvariant(), name.Trim(), Blank(loinc), Blank(unit), decimals,
            refLow, refHigh, criticalLow, criticalHigh, string.IsNullOrWhiteSpace(specimenType) ? "serum" : specimenType, Blank(instrument),
            deltaPercent, deltaAbsolute, deltaWindowDays <= 0 ? 30 : deltaWindowDays,
            Blank(panelComponents)?.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => c.ToUpperInvariant()).ToList()),
            User.Identity!.Name!, ct), ct);

    public Task<IActionResult> OnPostRollbackCatalogAsync(string code, int version, CancellationToken ct) =>
        Run(() => catalog.ProposeRollbackAsync(code, version, User.Identity!.Name!, ct), ct);

    public Task<IActionResult> OnPostRollbackMappingAsync(Guid id, CancellationToken ct) =>
        Run(() => mappings.ProposeRollbackAsync(id, User.Identity!.Name!, ct), ct);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public Task<IActionResult> OnPostApproveMappingAsync(Guid id, CancellationToken ct) => Run(() => mappings.ApproveAsync(id, User.Identity!.Name!, ct), ct);
    public Task<IActionResult> OnPostProposeMappingAsync(string instrumentId, string instrumentCode, string catalogCode, string resultType,
        decimal unitFactor, string? instrumentUnit, CancellationToken ct) =>
        Run(() => mappings.ProposeAsync(new MappingProposal(instrumentId, instrumentCode, catalogCode, resultType, unitFactor,
            string.IsNullOrWhiteSpace(instrumentUnit) ? null : instrumentUnit, true), User.Identity!.Name!, ct), ct);

    public Task<IActionResult> OnPostProposeRuleAsync(string catalogCode, string? instrumentId, decimal? limitLow, decimal? limitHigh, int qcWindowHours,
        string validationDocumentId, CancellationToken ct) =>
        Run(() => autoverify.ProposeAsync(new AutoverifyRuleProposal(catalogCode.Trim().ToUpperInvariant(), Blank(instrumentId), limitLow, limitHigh,
            qcWindowHours <= 0 ? 24 : qcWindowHours, validationDocumentId ?? ""), User.Identity!.Name!, ct), ct);

    public Task<IActionResult> OnPostApproveRuleAsync(Guid id, CancellationToken ct) => Run(() => autoverify.ApproveAsync(id, User.Identity!.Name!, ct), ct);

    public Task<IActionResult> OnPostDisableRuleAsync(Guid id, string reason, CancellationToken ct) => Run(() => autoverify.DisableAsync(id, User.Identity!.Name!, reason, ct), ct);
}
