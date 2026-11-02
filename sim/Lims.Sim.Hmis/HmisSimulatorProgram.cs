using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lims.Contracts;

namespace Lims.Sim.Hmis;

public static class HmisSimulatorProgram
{
    public static void Main(string[] args) => BuildApp(args).Run();

    public static WebApplication BuildApp(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<HmisSimulatorState>();
        builder.Services.AddHttpClient();
        configure?.Invoke(builder);
        var app = builder.Build();
        Map(app);
        return app;
    }

    public static void Map(WebApplication app)
    {
        var json = HmisSimulatorState.JsonOptions;

        app.MapPost("/oauth/token", (HttpRequest req, HmisSimulatorState s) =>
        {
            var form = req.Form;
            if (form["grant_type"] != "client_credentials" || form["client_id"] != s.ClientId || form["client_secret"] != s.ClientSecret)
                return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            s.Tokens[token] = DateTimeOffset.UtcNow.AddMinutes(60);
            return Results.Json(new { access_token = token, token_type = "Bearer", expires_in = 3600 });
        });

        var api = app.MapGroup("/api/lab/v1");
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var s = ctx.HttpContext.RequestServices.GetRequiredService<HmisSimulatorState>();
            if (!AuthenticationHeaderValue.TryParse(ctx.HttpContext.Request.Headers.Authorization, out var auth) || auth.Parameter is null
                || !s.Tokens.TryGetValue(auth.Parameter, out var exp) || exp < DateTimeOffset.UtcNow)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            if (s.Faults.SlowMs > 0) await Task.Delay(s.Faults.SlowMs);
            if (s.Faults.RateLimitNext > 0) { s.Faults.RateLimitNext--; ctx.HttpContext.Response.Headers.RetryAfter = "1"; return Results.StatusCode(429); }
            if (s.Faults.Fail500Next > 0) { s.Faults.Fail500Next--; return Results.StatusCode(500); }
            return await next(ctx);
        });

        api.MapGet("/orders", (DateTimeOffset? updated_since, string? cursor, int? limit, HmisSimulatorState s) =>
            Results.Json(s.QueryOrders(updated_since, cursor, Math.Clamp(limit ?? 100, 1, 100)), json));

        api.MapGet("/orders/{id}", (string id, HmisSimulatorState s) =>
            s.GetOrder(id) is { } o ? Results.Json(o, json) : Results.NotFound());

        api.MapPost("/orders/{id}/status", (string id) => Results.Accepted());

        api.MapGet("/test-catalog", () => Results.Json(new[]
        {
            new HmisCatalogEntry("TSH", "Thyroid stimulating hormone", "3016-3", "mIU/L"),
            new HmisCatalogEntry("FT4", "Free thyroxine", "3024-7", "pmol/L"),
            new HmisCatalogEntry("HBSAG", "Hepatitis B surface antigen", "5196-1", null),
        }, json));

        api.MapPost("/results", async (HttpContext ctx, HmisSimulatorState s) =>
        {
            Interlocked.Increment(ref s.PostResultCalls);
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrEmpty(key)) return Results.Json(new { error = "Idempotency-Key required" }, statusCode: 400);
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            HmisResultsRequest? req;
            try { req = JsonSerializer.Deserialize<HmisResultsRequest>(body, json); }
            catch (JsonException) { return Results.Json(new { error = "invalid JSON" }, statusCode: 422); }
            if (req?.Results is not { Count: > 0 and <= 50 }) return Results.Json(new { error = "1 to 50 results required" }, statusCode: 422);
            if (req.Results.Any(r => s.GetOrder(r.OrderId) is null))
                return Results.Json(new { error = "unknown order" }, statusCode: 422);

            var (status, response) = s.SubmitResults(key, body, b =>
            {
                var acks = req.Results.Select(r => new HmisResultAck(r.MiddlewareResultId, b.Add(r, null, null))).ToList();
                return (201, JsonSerializer.Serialize(new HmisResultsResponse(acks), json));
            });
            if (status == 201 && s.Faults.DropResponseAfterCommitNext > 0)
            {
                s.Faults.DropResponseAfterCommitNext--;
                ctx.Abort(); // committed, but the caller never sees the response
                return Results.Empty;
            }
            return Results.Content(response, "application/json", Encoding.UTF8, status);
        });

        api.MapPost("/results/{id}/amendments", async (string id, HttpContext ctx, HmisSimulatorState s) =>
        {
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrEmpty(key)) return Results.Json(new { error = "Idempotency-Key required" }, statusCode: 400);
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            var req = JsonSerializer.Deserialize<HmisAmendmentRequest>(body, json);
            if (req is null || string.IsNullOrWhiteSpace(req.Reason)) return Results.Json(new { error = "reason required" }, statusCode: 422);
            var (status, response) = s.SubmitResults(key, body, b =>
            {
                if (!b.Exists(id)) return (404, "{\"error\":\"original result not found\"}");
                var newId = b.Add(req.Result, id, req.Reason);
                return (201, JsonSerializer.Serialize(new { hmis_result_id = newId, amends = id }, json));
            });
            return Results.Content(response, "application/json", Encoding.UTF8, status);
        });

        api.MapGet("/results", (string? idempotency_key, HmisSimulatorState s) =>
        {
            if (!s.Faults.ReadBackEnabled) return Results.StatusCode(501);
            if (string.IsNullOrEmpty(idempotency_key)) return Results.BadRequest();
            var found = s.ResultsByKey(idempotency_key);
            return found.Count == 0
                ? Results.NotFound()
                : Results.Json(new HmisResultsResponse(found.Select(f => new HmisResultAck(f.Item.MiddlewareResultId, f.HmisResultId)).ToList()), json);
        });

        // ---- simulator control endpoints (test harness only)
        app.MapPost("/sim/orders", async (HmisOrder order, HmisSimulatorState s, IHttpClientFactory http) =>
        {
            var stored = s.UpsertOrder(order);
            if (s.MiddlewareWebhookUrl is { } url)
            {
                var ev = JsonSerializer.SerializeToUtf8Bytes(new HmisOrderEvent(order.Status == "cancelled" ? "order.cancelled" : "order.updated", order.Id, DateTimeOffset.UtcNow), json);
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var sig = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(s.WebhookSecret),
                    Encoding.UTF8.GetBytes(ts + ".").Concat(ev).ToArray())).ToLowerInvariant();
                using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(ev) };
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                msg.Headers.Add("X-Timestamp", ts);
                msg.Headers.Add("X-Signature", sig);
                try { await http.CreateClient().SendAsync(msg); } catch (HttpRequestException) { /* polling is the safety net */ }
            }
            return Results.Json(stored, json);
        });
        app.MapPost("/sim/faults", (SimFaults f, HmisSimulatorState s) =>
        {
            s.Faults.Fail500Next = f.Fail500Next; s.Faults.RateLimitNext = f.RateLimitNext;
            s.Faults.DropResponseAfterCommitNext = f.DropResponseAfterCommitNext; s.Faults.SlowMs = f.SlowMs; s.Faults.ReadBackEnabled = f.ReadBackEnabled;
            return Results.Ok(s.Faults);
        });
        app.MapGet("/sim/results", (HmisSimulatorState s) => Results.Json(s.AllResults(), json));
        app.MapPost("/sim/reset", (HmisSimulatorState s) => { s.Reset(); return Results.Ok(); });
    }
}
