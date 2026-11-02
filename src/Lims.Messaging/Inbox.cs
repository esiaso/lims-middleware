using System.Security.Cryptography;
using Dapper;
using Lims.Platform;
using Npgsql;

namespace Lims.Messaging;

public sealed class InboxItem
{
    public Guid Id { get; init; }
    public string Source { get; init; } = default!;
    public string Channel { get; init; } = default!;
    public string Kind { get; init; } = default!;
    public byte[] Raw { get; init; } = [];
    public string CorrelationId { get; init; } = default!;
    public int Attempts { get; init; }
    public DateTime ReceivedAt { get; init; }
}

public enum InboxDisposition { Processed, Duplicate, Quarantined }

public sealed record InboxResult(InboxDisposition Disposition, string? Note = null);

/// <summary>Handles one inbox message inside the same transaction that marks it processed.</summary>
public interface IInboxHandler
{
    bool CanHandle(string source, string kind);
    Task<InboxResult> HandleAsync(InboxItem item, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct);
}

/// <summary>Thrown by a handler for an error that retrying cannot fix (goes straight to dead letter).</summary>
public sealed class PermanentMessageException(string message) : Exception(message);

public sealed class InboxStore
{
    /// <summary>
    /// Stores a raw message. Returns false when the dedupe key already exists (the earlier copy is kept).
    /// Call inside the transaction whose commit must precede the transport ACK or HTTP 2xx.
    /// </summary>
    public async Task<(Guid Id, bool Inserted)> InsertAsync(NpgsqlConnection conn, NpgsqlTransaction? tx,
        string source, string channel, string kind, byte[] raw, string correlationId, string? dedupeKey = null)
    {
        var id = Guid.CreateVersion7();
        var inserted = await conn.ExecuteScalarAsync<Guid?>("""
            INSERT INTO messaging.inbox_message (id, source, channel, kind, raw, raw_sha256, dedupe_key, correlation_id)
            VALUES (@id, @source, @channel, @kind, @raw, @sha, @dedupeKey, @correlationId)
            ON CONFLICT (source, kind, dedupe_key) WHERE dedupe_key IS NOT NULL DO NOTHING
            RETURNING id
            """, new { id, source, channel, kind, raw, sha = SHA256.HashData(raw), dedupeKey, correlationId }, tx);
        return (inserted ?? id, inserted.HasValue);
    }

    public async Task<IReadOnlyList<Guid>> ClaimAsync(Db db, int max, TimeSpan lease, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var ids = await conn.QueryAsync<Guid>("""
            UPDATE messaging.inbox_message SET locked_until = now() + @lease
            WHERE id IN (
                SELECT id FROM messaging.inbox_message
                WHERE status = 'pending' AND next_attempt_at <= now()
                  AND (locked_until IS NULL OR locked_until < now())
                ORDER BY received_at
                FOR UPDATE SKIP LOCKED
                LIMIT @max)
            RETURNING id
            """, new { lease, max });
        return ids.ToList();
    }

    public Task<InboxItem?> LockForProcessingAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id) =>
        conn.QuerySingleOrDefaultAsync<InboxItem?>("""
            SELECT id, source, channel, kind, raw, correlation_id AS CorrelationId, attempts, received_at AS ReceivedAt
            FROM messaging.inbox_message WHERE id = @id AND status = 'pending' FOR UPDATE
            """, new { id }, tx);

    public Task MarkAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string status, string? note) =>
        conn.ExecuteAsync("""
            UPDATE messaging.inbox_message
            SET status = @status, processed_at = now(), locked_until = NULL, last_error = @note, attempts = attempts + 1
            WHERE id = @id
            """, new { id, status, note }, tx);

    /// <summary>Re-queue a processed, quarantined or failed message (authorized replay; caller audits it).</summary>
    public Task<int> RequeueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id) =>
        conn.ExecuteAsync("""
            UPDATE messaging.inbox_message
            SET status = 'pending', next_attempt_at = now(), locked_until = NULL, processed_at = NULL
            WHERE id = @id AND status IN ('failed','quarantined','processed')
            """, new { id }, tx);
}
