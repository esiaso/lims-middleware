using Lims.Clinical;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class DowntimeModel(ClinicalQueries queries, DowntimeService downtime, ResultReviewService review) : PageModel
{
    public IReadOnlyList<OrderRow> Unlinked { get; private set; } = [];
    public string? Message { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct) =>
        Unlinked = (await queries.OrdersAsync(ct)).Where(o => o.IsDowntime && o.LinkedHmisOrderId is null && o.Status != "cancelled").ToList();

    public async Task<IActionResult> OnPostRegisterAsync(string mrn, string? givenName, string? familyName, string? sex, DateOnly? birthDate,
        string tests, string priority, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Technologist)) return Forbid();
        try
        {
            var codes = tests.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToUpperInvariant()).ToList();
            var (_, dt) = await downtime.RegisterAsync(new DowntimeOrderRequest(mrn.Trim(), givenName, familyName, sex, birthDate, codes, priority), User.Identity!.Name!, ct);
            Message = $"Registered {dt}. Collect and print labels from the Orders page.";
        }
        catch (WorkflowException ex) { Message = ex.Message; }
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostLinkAsync(Guid orderId, string hmisOrderId, string reason, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Approver)) return Forbid();
        try { Message = $"Linked; {await review.LinkDowntimeOrderAsync(orderId, hmisOrderId.Trim(), User.Identity!.Name!, reason, ct)} held results queued for delivery."; }
        catch (WorkflowException ex) { Message = ex.Message; }
        await LoadAsync(ct);
        return Page();
    }
}
