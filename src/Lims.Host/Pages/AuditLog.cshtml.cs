using Dapper;
using Lims.Audit;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class AuditLogModel(Db db, AuditVerifier verifier, AuditWriter audit, AuditPackage package) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Accession { get; set; }
    [BindProperty(SupportsGet = true)] public string? Patient { get; set; }
    [BindProperty(SupportsGet = true)] public string? Instrument { get; set; }
    [BindProperty(SupportsGet = true)] public string? Actor { get; set; }
    [BindProperty(SupportsGet = true)] public string? CorrelationId { get; set; }
    [BindProperty(SupportsGet = true)] public string? EntityId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
    public IReadOnlyList<AuditEventRow> Rows { get; private set; } = [];
    public ChainVerification? Verification { get; private set; }
    public string? Message { get; private set; }

    private AuditFilter Filter => new(Accession, Patient, Instrument, Actor, CorrelationId, EntityId, Action, From, To);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Auditor)) return Forbid();
        Rows = await AuditSearch.FindAsync(db, Filter, 500, newestFirst: true, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostVerifyAsync(CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Auditor)) return Forbid();
        Verification = await verifier.VerifyAsync(ct);
        await audit.WriteAsync(db, new AuditEntry(User.Identity!.Name!, "audit.chain_verified", "audit_event", null,
            After: new { Verification.Valid, Verification.RowsChecked, Verification.FirstBadSeq }), ct);
        Rows = await AuditSearch.FindAsync(db, Filter, 500, newestFirst: true, ct);
        return Page();
    }

    /// <summary>Signed CSV or JSON export of every event matching the filter (not just the 500 shown).</summary>
    public async Task<IActionResult> OnPostExportAsync(string format, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Auditor)) return Forbid();
        try
        {
            var (zip, name) = await package.ExportAsync(Filter, format, User.Identity!.Name!, ct);
            return File(zip, "application/zip", name);
        }
        catch (WorkflowException ex)
        {
            Message = ex.Message;
            Rows = await AuditSearch.FindAsync(db, Filter, 500, newestFirst: true, ct);
            return Page();
        }
    }
}
