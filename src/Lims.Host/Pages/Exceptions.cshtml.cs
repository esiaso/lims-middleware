using Lims.Audit;
using Lims.Clinical;
using Lims.Messaging;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class ExceptionsModel(ClinicalQueries queries, Db db, InboxStore inbox, AuditWriter audit) : PageModel
{
    public IReadOnlyList<ExceptionRow> Rows { get; private set; } = [];
    public string? Message { get; private set; }
    public async Task OnGetAsync(CancellationToken ct) => Rows = await queries.OpenExceptionsAsync(ct);

    public async Task<IActionResult> OnPostReplayAsync(Guid inboxId, string reason, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.IntegrationOperator)) return Forbid();
        var n = await db.InTransactionAsync(async (c, t) =>
        {
            var changed = await inbox.RequeueAsync(c, t, inboxId);
            if (changed > 0) await audit.WriteAsync(c, t, new AuditEntry(User.Identity!.Name!, "inbox.replayed", "inbox_message", inboxId.ToString(), Reason: reason));
            return changed;
        }, ct);
        Message = n > 0 ? "Message queued for reprocessing." : "Message could not be requeued.";
        Rows = await queries.OpenExceptionsAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostResolveAsync(Guid id, string reason, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Approver)) return Forbid();
        try { await queries.ResolveExceptionAsync(id, User.Identity!.Name!, reason, audit, ct); }
        catch (WorkflowException ex) { Message = ex.Message; }
        Rows = await queries.OpenExceptionsAsync(ct);
        return Page();
    }
}
