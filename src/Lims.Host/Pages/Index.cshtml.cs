using Dapper;
using Lims.Audit;
using Lims.Hmis;
using Lims.Host.Api;
using Lims.Instruments;
using Lims.Instruments.Astm;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Lims.Host.Pages;

/// <summary>Integration health (design 8): a tile per link, queues, disk, backup, audit export lag; pause and resume a link.</summary>
public sealed class IndexModel(LinkHealthRegistry links, HmisHealth hmis, Db db, Lims.Instruments.Registration.InstrumentDirectory instruments,
    IOptions<BackupStatusOptions> backup, StorageGate storage, AuditWriter audit) : PageModel
{
    public IReadOnlyList<LinkHealth> Links { get; private set; } = [];
    public HmisHealth Hmis => hmis;
    public IDictionary<string, object?> Queues { get; private set; } = new Dictionary<string, object?>();
    public double? DiskFreePercent { get; private set; }
    public bool AcceptingMessages => storage.AcceptingNewMessages;
    public DateTimeOffset? LastBackupAt { get; private set; }
    public bool BackupConfigured => !string.IsNullOrWhiteSpace(backup.Value.Directory);
    public bool BackupStale => LastBackupAt is null || DateTimeOffset.UtcNow - LastBackupAt > backup.Value.MaxAge;
    public string? Message { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        Links = instruments.All.Select(i => links.Get(i.Id)).ToList();
        await using var conn = await db.OpenAsync(ct);
        Queues = (IDictionary<string, object?>)await conn.QuerySingleAsync("""
            SELECT
              (SELECT count(*) FROM messaging.inbox_message WHERE status='pending') AS "Inbox pending",
              (SELECT count(*) FROM messaging.outbox_message WHERE status IN ('pending','sent_unknown')) AS "Unsent results",
              (SELECT to_char(now() - min(created_at), 'HH24:MI:SS') FROM messaging.outbox_message WHERE status IN ('pending','sent_unknown')) AS "Oldest unsent age",
              (SELECT count(*) FROM messaging.dead_letter WHERE resolved_at IS NULL) AS "Dead letters",
              (SELECT count(*) FROM clinical.instrument_result WHERE disposition IN ('unmatched','quarantined') AND resolved_at IS NULL) AS "Open exceptions",
              (SELECT count(*) FROM clinical.result WHERE status IN ('received','reviewed')) AS "Awaiting review",
              (SELECT COALESCE(max(seq),0) FROM audit.audit_event) - COALESCE((SELECT max(last_seq) FROM audit.export_state), 0) AS "Audit export lag (events)"
            """);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory)!);
            DiskFreePercent = Math.Round(100.0 * drive.AvailableFreeSpace / drive.TotalSize, 1);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { DiskFreePercent = null; }
        LastBackupAt = BackupStatus.LastBackupAt(backup.Value);
    }

    public static string QueueLink(string key) => key switch
    {
        "Unsent results" => "/Deliveries",
        "Dead letters" or "Open exceptions" => "/Exceptions",
        "Awaiting review" => "/Review",
        "Audit export lag (events)" => "/AuditLog",
        _ => "",
    };

    public Task<IActionResult> OnPostPauseAsync(string id, string reason, CancellationToken ct) => SetPausedAsync(id, true, reason, ct);
    public Task<IActionResult> OnPostResumeAsync(string id, string reason, CancellationToken ct) => SetPausedAsync(id, false, reason, ct);

    /// <summary>
    /// A paused link refuses new messages (ASTM NAK at ENQ, HL7 AE 207) so the analyzer keeps its results, and sends nothing.
    /// Pausing is held in memory: a service restart resumes every link.
    /// </summary>
    private async Task<IActionResult> SetPausedAsync(string id, bool paused, string reason, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.IntegrationOperator)) return Forbid();
        if (instruments.Find(id) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(reason)) { Message = "A reason is required"; await LoadAsync(ct); return Page(); }
        var h = links.Get(id);
        h.Paused = paused;
        h.PausedBy = paused ? User.Identity!.Name : null;
        await audit.WriteAsync(db, new AuditEntry(User.Identity!.Name!, paused ? "instrument.link_paused" : "instrument.link_resumed", "instrument", id,
            Reason: reason.Trim()), ct);
        return RedirectToPage();
    }
}
