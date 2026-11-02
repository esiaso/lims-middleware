using Dapper;
using Lims.Platform;
using Npgsql;

namespace Lims.Messaging;

public sealed class OutboxItem
{
    public Guid Id { get; init; }
    public string Destination { get; init; } = default!;
    public string Kind { get; init; } = default!;
    public Guid AggregateId { get; init; }
    public string Payload { get; init; } = default!;
    public string IdempotencyKey { get; init; } = default!;
    public string CorrelationId { get; init; } = default!;
    public string Status { get; init; } = default!;
    public int Attempts { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class OutboxStore
{
    /// <summary>Enqueue in the SAME transaction as the state change that requires the delivery (design 7.2).</summary>
    public async Task<Guid> EnqueueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string destination, string kind,
        Guid aggregateId, Guid? approvalId, string payloadJson, string idempotencyKey, string correlationId, int priority)
    {
        var id = Guid.CreateVersion7();
        await conn.ExecuteAsync("""
            INSERT INTO messaging.outbox_message (id, destination, kind, aggregate_id, approval_id, payload, idempotency_key, correlation_id, priority)
            VALUES (@id, @destination, @kind, @aggregateId, @approvalId, @payloadJson::jsonb, @idempotencyKey, @correlationId, @priority)
            """, new { id, destination, kind, aggregateId, approvalId, payloadJson, idempotencyKey, correlationId, priority }, tx);
        return id;
    }

    public async Task<IReadOnlyList<OutboxItem>> ClaimAsync(Db db, string destination, int max, TimeSpan lease, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<OutboxItem>("""
            UPDATE messaging.outbox_message o SET locked_until = now() + @lease
            WHERE o.id IN (
                SELECT id FROM messaging.outbox_message
                WHERE destination = @destination AND status IN ('pending','sent_unknown') AND next_attempt_at <= now()
                  AND (locked_until IS NULL OR locked_until < now())
                ORDER BY priority DESC, created_at
                FOR UPDATE SKIP LOCKED
                LIMIT @max)
            RETURNING o.id, o.destination, o.kind, o.aggregate_id AS AggregateId, o.payload::text AS Payload,
                      o.idempotency_key AS IdempotencyKey, o.correlation_id AS CorrelationId, o.status, o.attempts, o.created_at AS CreatedAt
            """, new { destination, max, lease });
        return rows.OrderByDescending(r => r.Status == "sent_unknown").ThenBy(r => r.CreatedAt).ToList();
    }

    public Task MarkConfirmedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, string externalRef) =>
        conn.ExecuteAsync("""
            UPDATE messaging.outbox_message SET status='confirmed', external_ref=@externalRef, confirmed_at=now(),
                locked_until=NULL, last_error=NULL, attempts = attempts + 1
            WHERE id=@id
            """, new { id, externalRef }, tx);

    public Task MarkRetryAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, string status, TimeSpan delay, string error) =>
        conn.ExecuteAsync("""
            UPDATE messaging.outbox_message SET status=@status, next_attempt_at = now() + @delay, locked_until=NULL,
                last_error=@error, attempts = attempts + 1
            WHERE id=@id
            """, new { id, status, delay, error }, tx);

    public Task MarkTerminalAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, string status, string error) =>
        conn.ExecuteAsync("""
            UPDATE messaging.outbox_message SET status=@status, locked_until=NULL, last_error=@error, attempts = attempts + 1
            WHERE id=@id
            """, new { id, status, error }, tx);

    public Task<int> RequeueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id) =>
        conn.ExecuteAsync("""
            UPDATE messaging.outbox_message SET status = CASE WHEN status='manual_confirmation' THEN 'sent_unknown' ELSE 'pending' END,
                next_attempt_at=now(), locked_until=NULL
            WHERE id=@id AND status IN ('dead','manual_confirmation')
            """, new { id }, tx);
}

public sealed class DeadLetterStore
{
    public Task AddAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string origin, Guid originId, string reason) =>
        conn.ExecuteAsync("""
            INSERT INTO messaging.dead_letter (id, origin, origin_id, reason) VALUES (@id, @origin, @originId, @reason)
            """, new { id = Guid.CreateVersion7(), origin, originId, reason }, tx);

    public Task<int> ResolveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string user, string resolution) =>
        conn.ExecuteAsync("""
            UPDATE messaging.dead_letter SET resolved_at=now(), resolved_by=@user, resolution=@resolution
            WHERE id=@id AND resolved_at IS NULL
            """, new { id, user, resolution }, tx);
}
