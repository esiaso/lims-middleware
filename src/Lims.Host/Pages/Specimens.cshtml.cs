using Dapper;
using Lims.Clinical;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Lims.Host.Pages;

/// <summary>Specimen tracking and labels (design 8): timeline, accept, reject, recollection, reprint.</summary>
public sealed class SpecimensModel(Db db, SpecimenService specimens, IOptions<LabelPrinterOptions> printer) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Accession { get; set; }
    public IReadOnlyList<dynamic> Rows { get; private set; } = [];
    public dynamic? Selected { get; private set; }
    public IReadOnlyList<dynamic> Tests { get; private set; } = [];
    public IReadOnlyList<dynamic> Timeline { get; private set; } = [];
    public string? Message { get; private set; }
    public string? Zpl { get; private set; }
    public IReadOnlyList<string> Reasons => RejectionReasons.All;

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var q = string.IsNullOrWhiteSpace(Q) ? null : Q.Trim();
        var barcode = q is null ? null : AccessionNumber.NormalizeBarcode(q);
        Rows = (await conn.QueryAsync("""
            SELECT s.accession, s.barcode, s.specimen_type, s.status, s.collected_at, s.rejection_reason, p.mrn,
                   o.hmis_order_id, r.accession AS recollection_of
            FROM clinical.specimen s JOIN clinical.lab_order o ON o.id=s.order_id JOIN clinical.patient p ON p.id=o.patient_id
            LEFT JOIN clinical.specimen r ON r.id = s.recollection_of
            WHERE @q::text IS NULL OR p.mrn=@q OR o.hmis_order_id=@q OR s.barcode=@barcode OR s.accession=@q
            ORDER BY s.created_at DESC LIMIT 200
            """, new { q, barcode })).ToList();

        var selected = string.IsNullOrWhiteSpace(Accession) ? null : AccessionNumber.NormalizeBarcode(Accession);
        if (selected is null) return;
        Selected = await conn.QuerySingleOrDefaultAsync("""
            SELECT s.id, s.accession, s.barcode, s.specimen_type, s.status, s.rejection_reason, s.collected_at, s.collected_by,
                   s.received_at, s.received_by, p.mrn, p.given_name, p.family_name, o.hmis_order_id,
                   r.accession AS recollection_of, (SELECT n.accession FROM clinical.specimen n WHERE n.recollection_of=s.id LIMIT 1) AS recollected_as
            FROM clinical.specimen s JOIN clinical.lab_order o ON o.id=s.order_id JOIN clinical.patient p ON p.id=o.patient_id
            LEFT JOIN clinical.specimen r ON r.id = s.recollection_of
            WHERE s.barcode=@selected
            """, new { selected });
        if (Selected is null) return;
        Guid id = Selected.id;
        Tests = (await conn.QueryAsync("""
            SELECT ot.catalog_code, ot.panel_code, ot.status, st.instrument_id,
                   (SELECT r.status FROM clinical.result r WHERE r.specimen_test_id=st.id ORDER BY r.version DESC LIMIT 1) AS result_status
            FROM clinical.specimen_test st JOIN clinical.order_test ot ON ot.id=st.order_test_id
            WHERE st.specimen_id=@id ORDER BY ot.panel_code NULLS FIRST, ot.catalog_code
            """, new { id })).ToList();
        Timeline = (await conn.QueryAsync("""
            SELECT occurred_at, actor, action, reason FROM audit.audit_event
            WHERE (entity_type='specimen' AND entity_id=@sid) OR accession=@acc
            ORDER BY seq LIMIT 500
            """, new { sid = id.ToString(), acc = (string)Selected.accession })).ToList();
    }

    private async Task<IActionResult> Run(string accession, Func<string, Task> action, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Technologist)) return Forbid();
        Accession = accession;
        try { await action(User.Identity!.Name!); }
        catch (WorkflowException ex) { Message = ex.Message; }
        await LoadAsync(ct);
        return Page();
    }

    public Task<IActionResult> OnPostAcceptAsync(string accession, CancellationToken ct) =>
        Run(accession, u => specimens.AcceptAsync(accession, u, ct), ct);

    public Task<IActionResult> OnPostRejectAsync(string accession, string reason, CancellationToken ct) =>
        Run(accession, u => specimens.RejectAsync(accession, u, reason, ct), ct);

    public Task<IActionResult> OnPostRecollectAsync(string accession, string reason, CancellationToken ct) =>
        Run(accession, async u =>
        {
            await specimens.RequestRecollectionAsync(accession, u, reason, ct);
            Message = "Recollection requested. Collect the order again from the Orders page to issue the new tube.";
        }, ct);

    public Task<IActionResult> OnPostReprintAsync(string accession, CancellationToken ct) =>
        Run(accession, async u =>
        {
            Zpl = await specimens.ReprintLabelAsync(accession, u, ct);
            if (printer.Value.Host is { } host)
            {
                try { await ZplLabel.PrintAsync(host, printer.Value.Port, Zpl, ct); Message = "Label sent to the printer."; }
                catch (System.Net.Sockets.SocketException ex) { Message = $"Label printer: {ex.Message}"; }
            }
        }, ct);
}
