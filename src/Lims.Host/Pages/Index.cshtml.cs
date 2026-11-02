using Dapper;
using Lims.Hmis;
using Lims.Instruments;
using Lims.Instruments.Astm;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class IndexModel(LinkHealthRegistry links, HmisHealth hmis, Db db) : PageModel
{
    public IReadOnlyCollection<LinkHealth> Links => links.All;
    public HmisHealth Hmis => hmis;
    public IDictionary<string, object?> Queues { get; private set; } = new Dictionary<string, object?>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = (IDictionary<string, object?>)await conn.QuerySingleAsync("""
            SELECT
              (SELECT count(*) FROM messaging.inbox_message WHERE status='pending') AS "Inbox pending",
              (SELECT count(*) FROM messaging.outbox_message WHERE status IN ('pending','sent_unknown')) AS "Unsent results",
              (SELECT to_char(now() - min(created_at), 'HH24:MI:SS') FROM messaging.outbox_message WHERE status IN ('pending','sent_unknown')) AS "Oldest unsent age",
              (SELECT count(*) FROM messaging.dead_letter WHERE resolved_at IS NULL) AS "Dead letters",
              (SELECT count(*) FROM clinical.instrument_result WHERE disposition IN ('unmatched','quarantined') AND resolved_at IS NULL) AS "Open exceptions",
              (SELECT count(*) FROM clinical.result WHERE status IN ('received','reviewed')) AS "Awaiting review"
            """);
        Queues = row;
    }
}
