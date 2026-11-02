using Dapper;
using Lims.Audit;
using Lims.Platform;
using Microsoft.Extensions.Options;

namespace Lims.Host.Operations;

public sealed class ReconciliationOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan StatDeliveryAlert { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan RoutineDeliveryAlert { get; set; } = TimeSpan.FromMinutes(60);
    public TimeSpan CriticalEscalation { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan OrderNotCollected { get; set; } = TimeSpan.FromHours(24);
}

public sealed record ReconFinding(string Kind, string EntityType, string EntityId, string Detail, string Severity);

/// <summary>
/// Reconciliation jobs (design 7.7) and critical-result escalation (7.8). Each run lists what needs a person,
/// keeps one open item per problem, and closes items whose condition has cleared.
/// </summary>
public sealed class ReconciliationService(Db db, AuditWriter audit, IOptions<ReconciliationOptions> options, ILogger<ReconciliationService> logger) : BackgroundService
{
    public static readonly string[] Kinds =
        ["delivery_overdue", "critical_unacknowledged", "pending_at_instrument", "order_not_collected", "cancel_after_release", "downtime_unlinked"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Reconciliation run failed"); }
            await Task.Delay(options.Value.Interval, stoppingToken);
        }
    }

    public async Task<IReadOnlyList<ReconFinding>> FindAsync(CancellationToken ct)
    {
        var o = options.Value;
        await using var conn = await db.OpenAsync(ct);
        var findings = new List<ReconFinding>();

        findings.AddRange(await conn.QueryAsync<ReconFinding>("""
            SELECT 'delivery_overdue' AS Kind, 'result' AS EntityType, r.id::text AS EntityId,
                   format('%s %s approved %s ago, delivery %s', s.accession, ot.catalog_code,
                          date_trunc('minute', now() - rv.at), COALESCE(m.status, 'not queued')) AS Detail,
                   CASE WHEN m.status IN ('dead','manual_confirmation') THEN 'critical' ELSE 'warning' END AS Severity
            FROM clinical.result r
            JOIN clinical.review rv ON rv.result_id = r.id AND rv.action = 'approve'
            JOIN clinical.specimen_test st ON st.id = r.specimen_test_id
            JOIN clinical.specimen s ON s.id = st.specimen_id
            JOIN clinical.order_test ot ON ot.id = st.order_test_id
            JOIN clinical.lab_order o ON o.id = ot.order_id
            LEFT JOIN messaging.outbox_message m ON m.aggregate_id = r.id
            WHERE COALESCE(m.status, '') <> 'confirmed'
              AND NOT (o.is_downtime AND o.linked_hmis_order_id IS NULL)
              AND rv.at < now() - CASE WHEN o.priority = 'stat' THEN @stat ELSE @routine END
            """, new { stat = o.StatDeliveryAlert, routine = o.RoutineDeliveryAlert }));

        findings.AddRange(await conn.QueryAsync<ReconFinding>("""
            SELECT 'critical_unacknowledged' AS Kind, 'result' AS EntityType, r.id::text AS EntityId,
                   format('CRITICAL %s %s = %s %s waiting %s', s.accession, ot.catalog_code, r.value_text, COALESCE(r.units,''),
                          date_trunc('minute', now() - r.created_at)) AS Detail,
                   'critical' AS Severity
            FROM clinical.result r
            JOIN clinical.specimen_test st ON st.id = r.specimen_test_id
            JOIN clinical.specimen s ON s.id = st.specimen_id
            JOIN clinical.order_test ot ON ot.id = st.order_test_id
            WHERE r.is_critical AND r.status IN ('received','reviewed') AND r.created_at < now() - @esc
            """, new { esc = o.CriticalEscalation }));

        findings.AddRange(await conn.QueryAsync<ReconFinding>("""
            SELECT 'pending_at_instrument' AS Kind, 'order_test' AS EntityType, ot.id::text AS EntityId,
                   format('%s %s pending at instrument for %s (expected %s min)', s.accession, ot.catalog_code,
                          date_trunc('minute', now() - s.received_at), COALESCE(c.tat_minutes, 240)) AS Detail,
                   'warning' AS Severity
            FROM clinical.order_test ot
            JOIN clinical.specimen_test st ON st.order_test_id = ot.id
            JOIN clinical.specimen s ON s.id = st.specimen_id AND s.status IN ('accepted','on_instrument')
            LEFT JOIN clinical.test_catalog c ON c.code = ot.catalog_code AND c.status = 'active'
            WHERE ot.status = 'pending_at_instrument'
              AND s.received_at < now() - make_interval(mins => COALESCE(c.tat_minutes, 240))
            """));

        findings.AddRange(await conn.QueryAsync<ReconFinding>("""
            SELECT 'order_not_collected' AS Kind, 'lab_order' AS EntityType, o.id::text AS EntityId,
                   format('HMIS order %s registered %s ago with no specimen', o.hmis_order_id, date_trunc('hour', now() - o.created_at)) AS Detail,
                   'info' AS Severity
            FROM clinical.lab_order o
            WHERE o.status = 'registered' AND o.created_at < now() - @age
              AND NOT EXISTS (SELECT 1 FROM clinical.specimen s WHERE s.order_id = o.id)
            """, new { age = o.OrderNotCollected }));

        findings.AddRange(await conn.QueryAsync<ReconFinding>("""
            SELECT DISTINCT 'cancel_after_release' AS Kind, 'order_test' AS EntityType, a.entity_id AS EntityId,
                   format('HMIS cancelled a released test (order patient %s); an approver must decide', a.patient_id) AS Detail,
                   'critical' AS Severity
            FROM audit.audit_event a
            JOIN clinical.order_test ot ON ot.id::text = a.entity_id
            WHERE a.action = 'order_test.cancel_after_release' AND ot.status = 'released'
              AND NOT EXISTS (SELECT 1 FROM platform.reconciliation_item i
                              WHERE i.kind = 'cancel_after_release' AND i.entity_id = a.entity_id AND i.resolved_at IS NOT NULL)
            """));

        findings.AddRange(await conn.QueryAsync<ReconFinding>("""
            SELECT 'downtime_unlinked' AS Kind, 'lab_order' AS EntityType, o.id::text AS EntityId,
                   format('Downtime order %s not linked to an HMIS order', o.hmis_order_id) AS Detail,
                   'warning' AS Severity
            FROM clinical.lab_order o
            WHERE o.is_downtime AND o.linked_hmis_order_id IS NULL AND o.status <> 'cancelled'
            """));
        return findings;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var findings = await FindAsync(ct);
        var newCritical = new List<ReconFinding>();
        await db.InTransactionAsync(async (conn, tx) =>
        {
            foreach (var f in findings)
            {
                var inserted = await conn.ExecuteScalarAsync<bool>("""
                    INSERT INTO platform.reconciliation_item (id, kind, entity_type, entity_id, detail, severity)
                    VALUES (@id, @Kind, @EntityType, @EntityId, @Detail, @Severity)
                    ON CONFLICT (kind, entity_id) WHERE resolved_at IS NULL
                    DO UPDATE SET last_seen_at = now(), detail = EXCLUDED.detail, severity = EXCLUDED.severity
                    RETURNING (xmax = 0)
                    """, new { id = Guid.CreateVersion7(), f.Kind, f.EntityType, f.EntityId, f.Detail, f.Severity }, tx);
                if (inserted && f.Kind == "critical_unacknowledged") newCritical.Add(f);
            }
            // Close items whose condition cleared (cancel_after_release needs a person, never auto-closed).
            var open = findings.Select(f => f.Kind + "|" + f.EntityId).ToArray();
            await conn.ExecuteAsync("""
                UPDATE platform.reconciliation_item SET resolved_at = now(), resolved_by = 'system:reconciliation', resolution = 'condition cleared'
                WHERE resolved_at IS NULL AND kind <> 'cancel_after_release' AND NOT (kind || '|' || entity_id = ANY(@open))
                """, new { open }, tx);
            foreach (var c in newCritical)
            {
                await audit.WriteAsync(conn, tx, new AuditEntry("system:reconciliation", "result.critical_escalated", "result", c.EntityId, Reason: c.Detail));
            }
        }, ct);
        foreach (var c in newCritical)
            logger.LogCritical("Critical result not acknowledged, escalated to Lab Manager list: {Detail}", c.Detail);
        return findings.Count;
    }
}
