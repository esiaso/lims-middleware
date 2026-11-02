using Lims.Clinical;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class LabelPrinterOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 9100;
}

public sealed class OrdersModel(ClinicalQueries queries, SpecimenService specimens, Microsoft.Extensions.Options.IOptions<LabelPrinterOptions> printer) : PageModel
{
    public IReadOnlyList<OrderRow> Rows { get; private set; } = [];
    public IReadOnlyList<SpecimenCreated> Labels { get; private set; } = [];
    public string? Message { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => Rows = await queries.OrdersAsync(ct);

    public async Task<IActionResult> OnPostCollectAsync(Guid orderId, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Technologist)) return Forbid();
        try
        {
            Labels = await specimens.CollectAsync(orderId, User.Identity!.Name!, ct);
            if (printer.Value.Host is { } host)
                foreach (var l in Labels) await ZplLabel.PrintAsync(host, printer.Value.Port, l.Zpl, ct);
        }
        catch (WorkflowException ex) { Message = ex.Message; }
        catch (System.Net.Sockets.SocketException ex) { Message = $"Label printer: {ex.Message}"; }
        Rows = await queries.OrdersAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAcceptAsync(string accession, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Technologist)) return Forbid();
        try { await specimens.AcceptAsync(accession, User.Identity!.Name!, ct); Message = $"Accepted {accession}"; }
        catch (WorkflowException ex) { Message = ex.Message; }
        Rows = await queries.OrdersAsync(ct);
        return Page();
    }
}
