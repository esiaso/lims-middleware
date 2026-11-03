using Lims.Audit;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

/// <summary>Printable load list for one analyzer (design 4): accession barcode, patient initials and year of birth, tests. Audited.</summary>
public sealed class LoadListModel(Db db, AuditWriter audit) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Instrument { get; set; } = "";
    public IReadOnlyList<dynamic> Rows { get; private set; } = [];
    public DateTimeOffset PrintedAt { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Technologist) && !User.IsInRole(Roles.IntegrationOperator)) return Forbid();
        await using var conn = await db.OpenAsync(ct);
        Rows = (await AnalyzersModel.PendingWorklistAsync(conn, Instrument)).ToList();
        PrintedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));
        await audit.WriteAsync(db, new AuditEntry(User.Identity!.Name!, "instrument.load_list_printed", "instrument", Instrument,
            After: new { specimens = Rows.Select(r => (string)r.accession) }), ct);
        return Page();
    }
}
