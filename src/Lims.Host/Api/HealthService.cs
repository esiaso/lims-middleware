using Dapper;
using Lims.Hmis;
using Lims.Instruments;
using Lims.Platform;

namespace Lims.Host.Api;

/// <summary>Data for the integration health screen (design 8).</summary>
public sealed class HealthService(Db db, LinkHealthRegistry links, HmisHealth hmis)
{
    public async Task<object> SnapshotAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var q = await conn.QuerySingleAsync("""
            SELECT
              (SELECT count(*) FROM messaging.inbox_message WHERE status='pending') AS inbox_pending,
              (SELECT count(*) FROM messaging.outbox_message WHERE status IN ('pending','sent_unknown')) AS outbox_pending,
              (SELECT extract(epoch FROM now() - min(created_at))::bigint FROM messaging.outbox_message WHERE status IN ('pending','sent_unknown')) AS oldest_unsent_seconds,
              (SELECT count(*) FROM messaging.dead_letter WHERE resolved_at IS NULL) AS dead_letters,
              (SELECT count(*) FROM clinical.instrument_result WHERE disposition IN ('unmatched','quarantined') AND resolved_at IS NULL) AS open_exceptions,
              (SELECT count(*) FROM clinical.result WHERE status IN ('received','reviewed')) AS awaiting_review,
              (SELECT max(seq) FROM audit.audit_event) - COALESCE((SELECT max(last_seq) FROM audit.export_state), 0) AS audit_export_lag
            """);
        var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory)!);
        return new
        {
            links = links.All,
            hmis = new { hmis.LastPollAt, hmis.LastPollError, hmis.LastDeliveryAt, hmis.CircuitOpen, hmis.CircuitOpenUntil },
            queues = q,
            diskFreePercent = Math.Round(100.0 * drive.AvailableFreeSpace / drive.TotalSize, 1),
        };
    }
}
