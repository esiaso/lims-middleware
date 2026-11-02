using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Lims.Contracts;
using Lims.Platform;
using Microsoft.Extensions.Options;

namespace Lims.Hmis;

public abstract record DeliveryResult
{
    public sealed record Confirmed(string HmisRef) : DeliveryResult;
    public sealed record RejectedPermanent(string Reason, int? Status) : DeliveryResult;
    public sealed record RetryableFailure(string Reason, int? Status, TimeSpan? RetryAfter) : DeliveryResult;
    /// <summary>Timeout or lost response after the request may have reached the HMIS: read back before resending.</summary>
    public sealed record Unknown(string Reason) : DeliveryResult;
    public sealed record NotFound : DeliveryResult;
}

/// <summary>REST client for the PROPOSED lab API (design 6.9) with OAuth client credentials and a token-bucket limit.</summary>
public sealed class HmisClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HmisOptions _o;
    private readonly IClock _clock;
    private readonly TokenBucketRateLimiter _limiter;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public HmisClient(HttpClient http, IOptions<HmisOptions> options, IClock clock)
    {
        _http = http;
        _o = options.Value;
        _clock = clock;
        _http.BaseAddress ??= new Uri(_o.BaseUrl);
        _http.Timeout = _o.RequestTimeout;
        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = Math.Max(1, _o.RequestsPerMinute / 6),
            TokensPerPeriod = Math.Max(1, _o.RequestsPerMinute / 6),
            ReplenishmentPeriod = TimeSpan.FromSeconds(10),
            QueueLimit = 1000,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && _clock.UtcNow < _tokenExpires.AddSeconds(-60)) return _token;
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && _clock.UtcNow < _tokenExpires.AddSeconds(-60)) return _token;
            using var req = new HttpRequestMessage(HttpMethod.Post, _o.TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials", ["client_id"] = _o.ClientId, ["client_secret"] = _o.ClientSecret,
                }),
            };
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            _token = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            _tokenExpires = _clock.UtcNow.AddSeconds(expiresIn);
            return _token!;
        }
        finally { _tokenLock.Release(); }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        using var lease = await _limiter.AcquireAsync(1, ct);
        var req = build();
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) _token = null; // force refresh on next call
        return resp;
    }

    public async Task<HmisOrderPage> GetOrdersAsync(DateTimeOffset? updatedSince, string? cursor, int limit, CancellationToken ct)
    {
        var q = new StringBuilder($"orders?limit={limit}");
        if (updatedSince is { } u) q.Append("&updated_since=").Append(Uri.EscapeDataString(u.ToString("O")));
        if (cursor is not null) q.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, q.ToString()), ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<HmisOrderPage>(Json.Options, ct))!;
    }

    public async Task<HmisOrder?> GetOrderAsync(string id, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"orders/{Uri.EscapeDataString(id)}"), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<HmisOrder>(Json.Options, ct);
    }

    public async Task<IReadOnlyList<HmisCatalogEntry>> GetCatalogAsync(CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "test-catalog"), ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<List<HmisCatalogEntry>>(Json.Options, ct))!;
    }

    public Task<DeliveryResult> SubmitResultAsync(HmisResultItem item, string idempotencyKey, CancellationToken ct) =>
        PostAsync("results", Json.Serialize(new HmisResultsRequest([item])), idempotencyKey, item.MiddlewareResultId, ct);

    public Task<DeliveryResult> SubmitAmendmentAsync(HmisResultItem item, string originalHmisRef, string reason, string idempotencyKey, CancellationToken ct) =>
        PostAsync($"results/{Uri.EscapeDataString(originalHmisRef)}/amendments", Json.Serialize(new HmisAmendmentRequest(item, reason)), idempotencyKey, item.MiddlewareResultId, ct);

    public async Task<DeliveryResult> FindResultAsync(string idempotencyKey, Guid middlewareResultId, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"results?idempotency_key={Uri.EscapeDataString(idempotencyKey)}"), ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return new DeliveryResult.NotFound();
            return await InterpretAsync(resp, middlewareResultId, ct);
        }
        catch (Exception ex) when (IsTransport(ex, ct))
        {
            return new DeliveryResult.RetryableFailure($"read-back failed: {ex.Message}", null, null);
        }
    }

    private async Task<DeliveryResult> PostAsync(string path, string body, string idempotencyKey, Guid middlewareResultId, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.Add("Idempotency-Key", idempotencyKey);
                return req;
            }, ct);
            return await InterpretAsync(resp, middlewareResultId, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.NetworkUnreachable })
        {
            // Never connected, so the HMIS cannot have stored it.
            return new DeliveryResult.RetryableFailure($"cannot connect: {ex.Message}", null, null);
        }
        catch (Exception ex) when (IsTransport(ex, ct))
        {
            return new DeliveryResult.Unknown(ex.Message);
        }
    }

    private static bool IsTransport(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static async Task<DeliveryResult> InterpretAsync(HttpResponseMessage resp, Guid middlewareResultId, CancellationToken ct)
    {
        var status = (int)resp.StatusCode;
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("hmis_result_id", out var single)) return new DeliveryResult.Confirmed(single.GetString()!);
                if (root.TryGetProperty("results", out var arr))
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (el.TryGetProperty("middleware_result_id", out var mid) && mid.GetGuid() == middlewareResultId)
                            return new DeliveryResult.Confirmed(el.GetProperty("hmis_result_id").GetString()!);
                    }
                }
            }
            catch (JsonException) { }
            // 2xx without our reference: we cannot prove which record it stored; read back before trusting it.
            return new DeliveryResult.Unknown($"HTTP {status} without a result reference");
        }
        if (status == 429 || status >= 500)
        {
            TimeSpan? retryAfter = resp.Headers.RetryAfter?.Delta;
            return new DeliveryResult.RetryableFailure($"HTTP {status}: {Short(text)}", status, retryAfter);
        }
        if (status == 401 || status == 403) return new DeliveryResult.RetryableFailure($"HTTP {status} (credentials)", status, null);
        return new DeliveryResult.RejectedPermanent($"HTTP {status}: {Short(text)}", status);
    }

    private static string Short(string s) => s.Length > 500 ? s[..500] : s;

    public void Dispose()
    {
        _limiter.Dispose();
        _tokenLock.Dispose();
    }
}
