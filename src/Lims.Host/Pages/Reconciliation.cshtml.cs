using Dapper;
using Lims.Audit;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class ReconciliationModel(Db db, AuditWriter audit) : PageModel
{
    public IReadOnlyList<dynamic> Items { get; private set; } = [];
    public bool CanResolve => User.IsInRole(Roles.Approver) || User.IsInRole(Roles.IntegrationOperator) || User.IsInRole(Roles.LabManager);

    public async Task OnGetAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        Items = (await conn.QueryAsync("""
            SELECT id, kind, detail, severity, first_seen_at FROM platform.reconciliation_item
            WHERE resolved_at IS NULL ORDER BY severity='critical' DESC, first_seen_at
            """)).ToList();
    }

    public async Task<IActionResult> OnPostResolveAsync(Guid id, string reason, CancellationToken ct)
    {
        if (!CanResolve) return Forbid();
        await db.InTransactionAsync(async (c, t) =>
        {
            if (await c.ExecuteAsync("UPDATE platform.reconciliation_item SET resolved_at=now(), resolved_by=@u, resolution=@reason WHERE id=@id AND resolved_at IS NULL",
                    new { id, u = User.Identity!.Name, reason }, t) > 0)
                await audit.WriteAsync(c, t, new AuditEntry(User.Identity!.Name!, "reconciliation.resolved", "reconciliation_item", id.ToString(), Reason: reason));
        }, ct);
        return RedirectToPage();
    }
}
