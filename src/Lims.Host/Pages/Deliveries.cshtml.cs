using Lims.Clinical;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class DeliveriesModel(ClinicalQueries queries) : PageModel
{
    public IReadOnlyList<DeliveryRow> Rows { get; private set; } = [];
    public async Task OnGetAsync(CancellationToken ct) => Rows = await queries.DeliveriesAsync(ct);
}
