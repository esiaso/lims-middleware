using Lims.Clinical;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class ExceptionsModel(ClinicalQueries queries) : PageModel
{
    public IReadOnlyList<ExceptionRow> Rows { get; private set; } = [];
    public async Task OnGetAsync(CancellationToken ct) => Rows = await queries.OpenExceptionsAsync(ct);
}
