using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lims.Contracts;

namespace Lims.Sim.Hmis;

/// <summary>Fault switches (design 11.1): 500s, 429s, slow responses, accepted-but-response-dropped.</summary>
public sealed class SimFaults
{
    public int Fail500Next { get; set; }
    public int RateLimitNext { get; set; }
    public int DropResponseAfterCommitNext { get; set; }
    public int SlowMs { get; set; }
    public bool ReadBackEnabled { get; set; } = true;
}

public sealed record StoredResult(string HmisResultId, HmisResultItem Item, string IdempotencyKey, string? AmendsHmisResultId, string? Reason, DateTimeOffset StoredAt);

/// <summary>
/// In-memory HMIS implementing the PROPOSED lab API (design 6.9). For testing only: synthetic data,
/// no persistence. When the HMIS team publishes its OpenAPI file this simulator is updated from it.
/// </summary>
public sealed class HmisSimulatorState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HmisOrder> _orders = new();
    private readonly Dictionary<string, (string BodyHash, int Status, string Response)> _idempotency = new();
    private readonly List<StoredResult> _results = new();
    private int _nextResultId = 1000;

    public SimFaults Faults { get; } = new();
    public string ClientId { get; set; } = "lims-middleware";
    public string ClientSecret { get; set; } = "sim-secret";
    public string WebhookSecret { get; set; } = "sim-webhook-secret";
    public string? MiddlewareWebhookUrl { get; set; }
    public ConcurrentDictionary<string, DateTimeOffset> Tokens { get; } = new();
    public int PostResultCalls;

    public HmisOrder UpsertOrder(HmisOrder order)
    {
        lock (_gate)
        {
            var stamped = order with { UpdatedAt = DateTimeOffset.UtcNow };
            if (_orders.TryGetValue(order.Id, out var existing) && stamped.UpdatedAt <= existing.UpdatedAt)
                stamped = stamped with { UpdatedAt = existing.UpdatedAt.AddMilliseconds(1) };
            _orders[order.Id] = stamped;
            return stamped;
        }
    }

    public HmisOrder? GetOrder(string id) { lock (_gate) return _orders.GetValueOrDefault(id); }

    public HmisOrderPage QueryOrders(DateTimeOffset? since, string? cursor, int limit)
    {
        lock (_gate)
        {
            IEnumerable<HmisOrder> q = _orders.Values.OrderBy(o => o.UpdatedAt).ThenBy(o => o.Id, StringComparer.Ordinal);
            if (cursor is not null)
            {
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 2);
                var ticks = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                q = q.Where(o => o.UpdatedAt.UtcTicks > ticks || (o.UpdatedAt.UtcTicks == ticks && string.CompareOrdinal(o.Id, parts[1]) > 0));
            }
            else if (since is { } s) q = q.Where(o => o.UpdatedAt >= s);
            var page = q.Take(limit + 1).ToList();
            string? next = null;
            if (page.Count > limit)
            {
                page.RemoveAt(page.Count - 1);
                var last = page[^1];
                next = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{last.UpdatedAt.UtcTicks}|{last.Id}"));
            }
            return new HmisOrderPage(page, next);
        }
    }

    /// <summary>Stores the result and the idempotency record atomically (what the Laravel side should do in one MySQL transaction).</summary>
    public (int Status, string Body) SubmitResults(string key, string body, Func<StoredResultsBuilder, (int, string)> work)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        lock (_gate)
        {
            if (_idempotency.TryGetValue(key, out var prior))
                return prior.BodyHash == hash ? (prior.Status, prior.Response) : (409, "{\"error\":\"idempotency key reused with a different body\"}");
            var (status, response) = work(new StoredResultsBuilder(this, key));
            if (status is >= 200 and < 300) _idempotency[key] = (hash, status, response);
            return (status, response);
        }
    }

    public sealed class StoredResultsBuilder(HmisSimulatorState s, string key)
    {
        public string Add(HmisResultItem item, string? amends, string? reason)
        {
            var id = $"HR-{s._nextResultId++}";
            s._results.Add(new StoredResult(id, item, key, amends, reason, DateTimeOffset.UtcNow));
            return id;
        }

        public bool Exists(string hmisResultId) => s._results.Any(r => r.HmisResultId == hmisResultId);
    }

    public IReadOnlyList<StoredResult> ResultsByKey(string key) { lock (_gate) return _results.Where(r => r.IdempotencyKey == key).ToList(); }
    public IReadOnlyList<StoredResult> AllResults() { lock (_gate) return _results.ToList(); }

    public void Reset()
    {
        lock (_gate)
        {
            _orders.Clear(); _idempotency.Clear(); _results.Clear();
            Faults.Fail500Next = Faults.RateLimitNext = Faults.DropResponseAfterCommitNext = Faults.SlowMs = 0;
            Faults.ReadBackEnabled = true;
        }
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
