using System.Security.Cryptography;
using System.Text;
using Dapper;
using Lims.Contracts;
using Lims.Messaging;
using Lims.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lims.Hmis;

/// <summary>HMAC-SHA256 webhook signature: hex(HMAC(secret, timestamp + "." + body)) in X-Signature, unix seconds in X-Timestamp.</summary>
public static class WebhookSignature
{
    public static string Sign(string secret, string timestamp, byte[] body)
    {
        var data = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        return "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data)).ToLowerInvariant();
    }

    public static bool Verify(string secret, string? timestamp, string? signature, byte[] body, DateTimeOffset now, TimeSpan maxSkew)
    {
        if (string.IsNullOrEmpty(secret) || timestamp is null || signature is null) return false;
        if (!long.TryParse(timestamp, out var ts)) return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(ts)).Duration() > maxSkew) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(secret, timestamp, body));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
    }
}

public static class HmisInbox
{
    public static async Task<bool> StoreOrderAsync(InboxStore inbox, NpgsqlConnection conn, NpgsqlTransaction tx, HmisOrder order)
    {
        var raw = Encoding.UTF8.GetBytes(Json.Serialize(order));
        var (_, inserted) = await inbox.InsertAsync(conn, tx, MessageSources.Hmis, "hmis", InboxKinds.HmisOrder, raw,
            Guid.CreateVersion7().ToString(), dedupeKey: $"{order.Id}@{order.UpdatedAt.UtcTicks}");
        return inserted;
    }
}

/// <summary>Polling safety net (design 6.7): pages through changed orders with a persisted cursor; each page commits orders and cursor together.</summary>
public sealed class HmisOrderPoller(Db db, HmisClient client, InboxStore inbox, IOptions<HmisOptions> options, HmisHealth health, ILogger<HmisOrderPoller> logger) : BackgroundService
{
    private const string CursorName = "orders";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.PollingEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var n = await PollOnceAsync(stoppingToken);
                health.LastPollAt = DateTimeOffset.UtcNow;
                health.LastPollError = null;
                if (n > 0) logger.LogInformation("Order poll stored {Count} order versions", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                health.LastPollError = ex.Message;
                logger.LogWarning("Order poll failed: {Error}", ex.Message);
            }
            await Task.Delay(options.Value.PollInterval, stoppingToken);
        }
    }

    public async Task<int> PollOnceAsync(CancellationToken ct)
    {
        var stored = 0;
        for (var page = 0; page < 100; page++)
        {
            (string? cursor, DateTime? since) state;
            await using (var conn = await db.OpenAsync(ct))
                state = await conn.QuerySingleOrDefaultAsync<(string?, DateTime?)>(
                    "SELECT cursor, updated_since FROM hmis.poll_cursor WHERE name=@n", new { n = CursorName });
            var since = state.since is { } s ? new DateTimeOffset(DateTime.SpecifyKind(s, DateTimeKind.Utc)) : (DateTimeOffset?)null;
            var result = await client.GetOrdersAsync(state.cursor is null ? since : null, state.cursor, options.Value.PageSize, ct);

            await db.InTransactionAsync(async (conn, tx) =>
            {
                foreach (var o in result.Data)
                    if (await HmisInbox.StoreOrderAsync(inbox, conn, tx, o)) stored++;
                var maxUpdated = result.Data.Count > 0 ? result.Data.Max(o => o.UpdatedAt).UtcDateTime : state.since;
                await conn.ExecuteAsync("""
                    INSERT INTO hmis.poll_cursor (name, cursor, updated_since, updated_at) VALUES (@n, @c, @u, now())
                    ON CONFLICT (name) DO UPDATE SET cursor=EXCLUDED.cursor, updated_since=COALESCE(EXCLUDED.updated_since, hmis.poll_cursor.updated_since), updated_at=now()
                    """, new { n = CursorName, c = result.NextCursor, u = maxUpdated }, tx);
            }, ct);
            if (result.NextCursor is null) break;
        }
        return stored;
    }
}

/// <summary>Thin webhook events carry only the order id: fetch the full order and queue it for registration.</summary>
public sealed class HmisOrderEventHandler(HmisClient client, InboxStore inbox) : IInboxHandler
{
    public bool CanHandle(string source, string kind) => source == MessageSources.Hmis && kind == InboxKinds.HmisOrderEvent;

    public async Task<InboxResult> HandleAsync(InboxItem item, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        HmisOrderEvent ev;
        try { ev = Json.Deserialize<HmisOrderEvent>(Encoding.UTF8.GetString(item.Raw)); }
        catch (Exception ex) { throw new PermanentMessageException($"Invalid webhook event: {ex.Message}"); }
        var order = await client.GetOrderAsync(ev.OrderId, ct) ?? throw new PermanentMessageException($"HMIS order {ev.OrderId} not found");
        var inserted = await HmisInbox.StoreOrderAsync(inbox, conn, tx, order);
        return new InboxResult(inserted ? InboxDisposition.Processed : InboxDisposition.Duplicate);
    }
}

public sealed class HmisHealth
{
    public DateTimeOffset? LastPollAt { get; set; }
    public string? LastPollError { get; set; }
    public DateTimeOffset? LastDeliveryAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? CircuitOpenUntil { get; set; }
    public bool CircuitOpen => CircuitOpenUntil is { } u && u > DateTimeOffset.UtcNow;
}
