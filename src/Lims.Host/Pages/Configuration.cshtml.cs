using Dapper;
using Lims.Clinical;
using Lims.Instruments;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class ConfigurationModel(Db db, CatalogService catalog, MappingService mappings) : PageModel
{
    public IReadOnlyList<dynamic> Catalog { get; private set; } = [];
    public IReadOnlyList<dynamic> Mappings { get; private set; } = [];
    public string? Message { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        Catalog = (await conn.QueryAsync("SELECT * FROM clinical.test_catalog WHERE status IN ('active','proposed') ORDER BY code, version")).ToList();
        Mappings = (await conn.QueryAsync("SELECT * FROM instruments.test_mapping WHERE status IN ('active','proposed') ORDER BY instrument_id, instrument_code, version")).ToList();
    }

    private async Task<IActionResult> Run(Func<Task> action, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.LabManager)) return Forbid();
        try { await action(); return RedirectToPage(); }
        catch (WorkflowException ex) { Message = ex.Message; await LoadAsync(ct); return Page(); }
    }

    public Task<IActionResult> OnPostApproveCatalogAsync(string code, CancellationToken ct) => Run(() => catalog.ApproveAsync(code, User.Identity!.Name!, ct), ct);
    public Task<IActionResult> OnPostApproveMappingAsync(Guid id, CancellationToken ct) => Run(() => mappings.ApproveAsync(id, User.Identity!.Name!, ct), ct);
    public Task<IActionResult> OnPostProposeMappingAsync(string instrumentId, string instrumentCode, string catalogCode, string resultType,
        decimal unitFactor, string? instrumentUnit, CancellationToken ct) =>
        Run(() => mappings.ProposeAsync(new MappingProposal(instrumentId, instrumentCode, catalogCode, resultType, unitFactor,
            string.IsNullOrWhiteSpace(instrumentUnit) ? null : instrumentUnit, true), User.Identity!.Name!, ct), ct);
}
