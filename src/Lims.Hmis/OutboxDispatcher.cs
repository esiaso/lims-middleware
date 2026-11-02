using Dapper;
using Lims.Audit;
using Lims.Contracts;
using Lims.Messaging;
using Lims.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lims.Hmis;

/// <summary>
/// Delivers approved results to the HMIS (design 7.1 steps 9-10, 7.4, 7.5). Every attempt is recorded;
/// an Unknown outcome is always followed by read-back before any resend, with the same idempotency key.
/// </summary>
public sealed class OutboxDispatcher(
    Db db, OutboxStore outbox, DeadLetterStore deadLetters, HmisClient client, AuditWriter audit,
    IOptions<HmisOptions> options, HmisHealth health, IClock clock, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var n = 0;
            try { n = await DispatchBatchAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Outbox dispatch failed"); }
            if (n == 0) await Task.Delay(options.Value.DispatchIdleDelay, stoppingToken);
        }
    }

    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        if (health.CircuitOpen) return 0;
        var items = await outbox.ClaimAsync(db, "hmis", 10, TimeSpan.FromMinutes(5), ct);
        foreach (var item in items)
        {
            if (health.CircuitOpen) break;
            await DispatchOneAsync(item, ct);
        }
        return items.Count;
    }

    private async Task DispatchOneAsync(OutboxItem item, CancellationToken ct)
    {
        var o = options.Value;
        var payload = Json.Deserialize<ResultDeliveryPayload>(item.Payload);
        var attemptNo = item.Attempts + 1;

        // 1. A previous attempt ended Unknown: prove whether the HMIS has it before sending again.
        if (item.Status == "sent_unknown")
        {
            if (!o.SupportsReadBack)
            {
                await FinishAsync(item, "manual_confirmation", "HMIS has no read-back: confirm manually before resending", null, ct);
                return;
            }
            var started = clock.UtcNow;
            var rb = await client.FindResultAsync(item.IdempotencyKey, payload.Result.MiddlewareResultId, ct);
            await RecordAttemptAsync(item.Id, attemptNo, "read_back", started, rb, ct);
            switch (rb)
            {
                case DeliveryResult.Confirmed c:
                    await ConfirmAsync(item, payload, c.HmisRef, "read_back", ct);
                    return;
                case DeliveryResult.NotFound:
                    break; // safe to resend with the same key
                default:
                    await RetryAsync(item, "sent_unknown", rb, ct);
                    return;
            }
        }

        // 2. Submit (or amend)
        string? originalRef = null;
        if (item.Kind == OutboxKinds.HmisAmendment)
        {
            await using var conn = await db.OpenAsync(ct);
            originalRef = await conn.ExecuteScalarAsync<string?>("""
                SELECT external_ref FROM messaging.outbox_message
                WHERE aggregate_id=@id AND status='confirmed' ORDER BY confirmed_at DESC LIMIT 1
                """, new { id = payload.SupersedesResultId });
            if (originalRef is null)
            {
                await RetryAsync(item, "pending", new DeliveryResult.RetryableFailure("original result not yet HMIS-confirmed", null, TimeSpan.FromMinutes(1)), ct);
                return;
            }
        }

        var sentAt = clock.UtcNow;
        var result = item.Kind == OutboxKinds.HmisAmendment
            ? await client.SubmitAmendmentAsync(payload.Result, originalRef!, payload.AmendmentReason ?? "correction", item.IdempotencyKey, ct)
            : await client.SubmitResultAsync(payload.Result, item.IdempotencyKey, ct);
        await RecordAttemptAsync(item.Id, attemptNo, item.Kind == OutboxKinds.HmisAmendment ? "amend" : "submit", sentAt, result, ct);

        switch (result)
        {
            case DeliveryResult.Confirmed c:
                health.ConsecutiveFailures = 0;
                await ConfirmAsync(item, payload, c.HmisRef, "submit", ct);
                break;
            case DeliveryResult.RejectedPermanent p:
                health.ConsecutiveFailures = 0;
                await FinishAsync(item, "dead", p.Reason, p.Reason, ct);
                break;
            case DeliveryResult.Unknown u:
                NoteFailure();
                await RetryAsync(item, "sent_unknown", u, ct, immediate: true);
                break;
            default:
                NoteFailure();
                await RetryAsync(item, "pending", result, ct);
                break;
        }
    }

    private void NoteFailure()
    {
        health.ConsecutiveFailures++;
        if (health.ConsecutiveFailures >= options.Value.CircuitBreakerFailures)
        {
            health.CircuitOpenUntil = DateTimeOffset.UtcNow + options.Value.CircuitBreakerPause;
            health.ConsecutiveFailures = options.Value.CircuitBreakerFailures - 1; // one trial request after the pause
            logger.LogWarning("HMIS circuit open until {Until}", health.CircuitOpenUntil);
        }
    }

    private Task ConfirmAsync(OutboxItem item, ResultDeliveryPayload payload, string hmisRef, string how, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await outbox.MarkConfirmedAsync(conn, tx, item.Id, hmisRef);
            await audit.WriteAsync(conn, tx, new AuditEntry("system:hmis-delivery", "result.hmis_confirmed", "result", item.AggregateId.ToString(),
                Accession: payload.Result.Accession, PatientId: payload.Result.Mrn, CorrelationId: item.CorrelationId,
                After: new { hmis_result_id = hmisRef, how, item.IdempotencyKey }));
            health.LastDeliveryAt = DateTimeOffset.UtcNow;
        }, ct);

    private async Task RetryAsync(OutboxItem item, string status, DeliveryResult r, CancellationToken ct, bool immediate = false)
    {
        var o = options.Value;
        var reason = r switch
        {
            DeliveryResult.RetryableFailure f => f.Reason,
            DeliveryResult.Unknown u => $"unknown outcome: {u.Reason}",
            _ => r.ToString(),
        };
        var tooOld = clock.UtcNow - new DateTimeOffset(DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Utc)) > o.MaxRetryAge;
        if (item.Attempts + 1 >= o.MaxAttempts || tooOld)
        {
            await FinishAsync(item, status == "sent_unknown" ? "manual_confirmation" : "dead", $"retries exhausted: {reason}", reason, ct);
            return;
        }
        var delay = immediate ? TimeSpan.Zero
            : r is DeliveryResult.RetryableFailure { RetryAfter: { } ra } ? ra
            : Backoff.Delay(item.Attempts + 1);
        await using var conn = await db.OpenAsync(ct);
        await outbox.MarkRetryAsync(conn, null, item.Id, status, delay, reason);
    }

    private Task FinishAsync(OutboxItem item, string status, string error, string? deadReason, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await outbox.MarkTerminalAsync(conn, tx, item.Id, status, error);
            await deadLetters.AddAsync(conn, tx, "outbox", item.Id, deadReason ?? error);
            await audit.WriteAsync(conn, tx, new AuditEntry("system:hmis-delivery", status == "dead" ? "delivery.dead_lettered" : "delivery.manual_confirmation",
                "result", item.AggregateId.ToString(), CorrelationId: item.CorrelationId, Reason: error));
        }, ct);

    private async Task RecordAttemptAsync(Guid outboxId, int attemptNo, string operation, DateTimeOffset started, DeliveryResult r, CancellationToken ct)
    {
        var (outcome, status, detail) = r switch
        {
            DeliveryResult.Confirmed c => ("confirmed", (int?)200, c.HmisRef),
            DeliveryResult.RejectedPermanent p => ("rejected", p.Status, p.Reason),
            DeliveryResult.RetryableFailure f => ("retryable", f.Status, f.Reason),
            DeliveryResult.Unknown u => ("unknown", null, u.Reason),
            DeliveryResult.NotFound => ("not_found", 404, null),
            _ => ("other", null, null),
        };
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO hmis.delivery_attempt (outbox_id, attempt_no, operation, started_at, finished_at, outcome, http_status, detail)
            VALUES (@outboxId, @attemptNo, @operation, @started, now(), @outcome, @status, @detail)
            """, new { outboxId, attemptNo, operation, started = started.UtcDateTime, outcome, status, detail });
    }
}
