using Lims.Clinical;
using Lims.Host.Api;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class ReviewModel(ClinicalQueries queries, ResultReviewService review, IClock clock) : PageModel
{
    public IReadOnlyList<ReviewQueueRow> Rows { get; private set; } = [];
    public string? Message { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => Rows = await queries.ReviewQueueAsync(ct);

    public Task<IActionResult> OnPostReviewAsync(Guid id, string? comment, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Technologist)) return Task.FromResult<IActionResult>(Forbid());
        return Run(() => review.ReviewAsync(id, User.UserName(), string.IsNullOrWhiteSpace(comment) ? null : comment, ct), ct);
    }

    public Task<IActionResult> OnPostApproveAsync(Guid id, string? notifiedPerson, string? method, bool readBack, string? deltaComment, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Approver)) return Task.FromResult<IActionResult>(Forbid());
        if (!ApiEndpoints.SignedInRecently(User, clock)) return Task.FromResult<IActionResult>(Redirect("/Login?returnUrl=/Review"));
        var critical = notifiedPerson is null ? null : new CriticalNotification(notifiedPerson, method ?? "phone", clock.UtcNow, readBack);
        return Run(() => review.ApproveAsync(id, User.UserName(), critical, ct, deltaComment), ct);
    }

    public Task<IActionResult> OnPostRejectAsync(Guid id, string reason, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Approver)) return Task.FromResult<IActionResult>(Forbid());
        return Run(() => review.RejectAsync(id, User.UserName(), reason, ct), ct);
    }

    private async Task<IActionResult> Run(Func<Task> action, CancellationToken ct)
    {
        try { await action(); return RedirectToPage(); }
        catch (WorkflowException ex) { Message = ex.Message; Rows = await queries.ReviewQueueAsync(ct); return Page(); }
    }
}
