using Lims.Audit;
using Lims.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lims.Messaging;

public sealed class InboxProcessorOptions
{
    public int BatchSize { get; set; } = 20;
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(1);
    public int MaxAttempts { get; set; } = 10;
}

/// <summary>
/// Claims pending inbox rows with FOR UPDATE SKIP LOCKED and runs the matching handler in one transaction
/// with the status change, so a crash re-processes rather than loses (at-least-once, idempotent handlers).
/// </summary>
public sealed class InboxProcessor(
    Db db, InboxStore inbox, DeadLetterStore deadLetters, AuditWriter audit, IEnumerable<IInboxHandler> handlers,
    IOptions<InboxProcessorOptions> options, ILogger<InboxProcessor> logger) : BackgroundService
{
    private readonly IReadOnlyList<IInboxHandler> _handlers = handlers.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int processed;
            try { processed = await ProcessBatchAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Inbox batch failed");
                processed = 0;
            }
            if (processed == 0) await Task.Delay(options.Value.IdleDelay, stoppingToken);
        }
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var ids = await inbox.ClaimAsync(db, options.Value.BatchSize, options.Value.Lease, ct);
        foreach (var id in ids) await ProcessOneAsync(id, ct);
        return ids.Count;
    }

    private async Task ProcessOneAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await db.InTransactionAsync(async (conn, tx) =>
            {
                var item = await inbox.LockForProcessingAsync(conn, tx, id);
                if (item is null) return; // already handled by someone else
                var handler = _handlers.FirstOrDefault(h => h.CanHandle(item.Source, item.Kind))
                    ?? throw new PermanentMessageException($"No handler for {item.Source}/{item.Kind}");
                var result = await handler.HandleAsync(item, conn, tx, ct);
                var status = result.Disposition switch
                {
                    InboxDisposition.Duplicate => "duplicate",
                    InboxDisposition.Quarantined => "quarantined",
                    _ => "processed",
                };
                await inbox.MarkAsync(conn, tx, id, status, result.Note);
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordFailureAsync(id, ex, ct);
        }
    }

    private async Task RecordFailureAsync(Guid id, Exception ex, CancellationToken ct)
    {
        var permanent = ex is PermanentMessageException;
        logger.LogWarning(ex, "Inbox message {InboxId} failed (permanent={Permanent})", id, permanent);
        await db.InTransactionAsync(async (conn, tx) =>
        {
            var attempts = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn,
                "UPDATE messaging.inbox_message SET attempts = attempts + 1, last_error = @e, locked_until = NULL WHERE id=@id RETURNING attempts",
                new { id, e = Truncate(ex.Message) }, tx);
            if (permanent || attempts >= options.Value.MaxAttempts)
            {
                await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE messaging.inbox_message SET status='failed' WHERE id=@id", new { id }, tx);
                await deadLetters.AddAsync(conn, tx, "inbox", id, Truncate(ex.Message));
                await audit.WriteAsync(conn, tx, new AuditEntry("system:inbox", "inbox.dead_lettered", "inbox_message", id.ToString(), Reason: Truncate(ex.Message)));
            }
            else
            {
                await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE messaging.inbox_message SET next_attempt_at = now() + @d WHERE id=@id",
                    new { id, d = Backoff.Delay(attempts) }, tx);
            }
        }, ct);
    }

    private static string Truncate(string s) => s.Length > 2000 ? s[..2000] : s;
}
