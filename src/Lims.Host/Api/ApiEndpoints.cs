using System.Security.Claims;
using Dapper;
using Lims.Audit;
using Lims.Clinical;
using Lims.Contracts;
using Lims.Hmis;
using Lims.Host.Identity;
using Lims.Instruments;
using Lims.Messaging;
using Lims.Platform;
using Microsoft.Extensions.Options;

namespace Lims.Host.Api;

public sealed record RejectSpecimenRequest(string Reason);
public sealed record ApproveRequest(CriticalNotification? Critical);
public sealed record ReasonRequest(string Reason);
public sealed record CommentRequest(string? Comment);
public sealed record LinkDowntimeRequest(string HmisOrderId, string Reason);
public sealed record CreateUserRequest(string Username, string DisplayName, string Password, string[] Roles);

public static class ApiEndpoints
{
    /// <summary>Approval needs a sign-in younger than this (design 8, "fresh re-authentication").</summary>
    public static readonly TimeSpan ApprovalReauthWindow = TimeSpan.FromMinutes(15);

    public static void Map(WebApplication app)
    {
        // ------------------------------------------------------------ health (design 10.4)
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
        app.MapGet("/health/ready", async (Db db, CancellationToken ct) =>
        {
            try
            {
                await using var conn = await db.OpenAsync(ct);
                await conn.ExecuteScalarAsync<int>("SELECT 1");
                return Results.Ok(new { status = "ready" });
            }
            catch (Exception) { return Results.Json(new { status = "database unavailable" }, statusCode: 503); }
        }).AllowAnonymous();
        app.MapGet("/health/details", async (HealthService health, CancellationToken ct) => Results.Ok(await health.SnapshotAsync(ct)))
            .RequireAuthorization("any");

        // ------------------------------------------------------------ HMIS webhook (design 6.9), HMAC signed
        app.MapPost("/api/hmis/v1/events", async (HttpRequest req, IOptions<HmisOptions> opt, InboxStore inbox, Db db, IClock clock, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms, ct);
            var body = ms.ToArray();
            if (body.Length > 1_048_576) return Results.StatusCode(413);
            if (!WebhookSignature.Verify(opt.Value.WebhookSecret, req.Headers["X-Timestamp"], req.Headers["X-Signature"], body, clock.UtcNow, opt.Value.WebhookMaxSkew))
                return Results.Unauthorized();
            HmisOrderEvent ev;
            try { ev = Json.Deserialize<HmisOrderEvent>(System.Text.Encoding.UTF8.GetString(body)); }
            catch (Exception) { return Results.BadRequest(); }
            // Stored before 2xx so an acknowledged event is never lost.
            await db.InTransactionAsync(async (c, t) =>
                await inbox.InsertAsync(c, t, MessageSources.Hmis, "hmis", InboxKinds.HmisOrderEvent, body, Guid.CreateVersion7().ToString(),
                    dedupeKey: $"{ev.OrderId}@{ev.OccurredAt.UtcTicks}@{ev.Event}"), ct);
            return Results.Accepted();
        }).AllowAnonymous();

        var api = app.MapGroup("/api").RequireAuthorization("any");

        // ------------------------------------------------------------ orders and specimens
        api.MapGet("/orders", (ClinicalQueries q, CancellationToken ct) => q.OrdersAsync(ct));
        api.MapPost("/orders/{orderId:guid}/collect", async (Guid orderId, ClaimsPrincipal u, SpecimenService s, CancellationToken ct) =>
            Results.Ok(await s.CollectAsync(orderId, u.UserName(), ct))).RequireAuthorization(Roles.Technologist);
        api.MapPost("/specimens/{accession}/accept", async (string accession, ClaimsPrincipal u, SpecimenService s, CancellationToken ct) =>
        { await s.AcceptAsync(accession, u.UserName(), ct); return Results.NoContent(); }).RequireAuthorization(Roles.Technologist);
        api.MapPost("/specimens/{accession}/reject", async (string accession, RejectSpecimenRequest r, ClaimsPrincipal u, SpecimenService s, CancellationToken ct) =>
        { await s.RejectAsync(accession, u.UserName(), r.Reason, ct); return Results.NoContent(); }).RequireAuthorization(Roles.Technologist);
        api.MapPost("/specimens/{accession}/reprint", async (string accession, ClaimsPrincipal u, SpecimenService s, CancellationToken ct) =>
            Results.Text(await s.ReprintLabelAsync(accession, u.UserName(), ct), "text/plain")).RequireAuthorization(Roles.Technologist);
        api.MapPost("/downtime-orders", async (DowntimeOrderRequest r, ClaimsPrincipal u, DowntimeService s, CancellationToken ct) =>
        {
            var (orderId, dt) = await s.RegisterAsync(r, u.UserName(), ct);
            return Results.Ok(new { orderId, downtimeOrderId = dt });
        }).RequireAuthorization(Roles.Technologist);
        api.MapPost("/orders/{orderId:guid}/link-downtime", async (Guid orderId, LinkDowntimeRequest r, ClaimsPrincipal u, ResultReviewService s, CancellationToken ct) =>
            Results.Ok(new { released = await s.LinkDowntimeOrderAsync(orderId, r.HmisOrderId, u.UserName(), r.Reason, ct) }))
            .RequireAuthorization(Roles.Approver);

        // ------------------------------------------------------------ results
        api.MapGet("/results/queue", (ClinicalQueries q, CancellationToken ct) => q.ReviewQueueAsync(ct));
        api.MapGet("/deliveries", (ClinicalQueries q, CancellationToken ct) => q.DeliveriesAsync(ct));
        api.MapPost("/results/{id:guid}/review", async (Guid id, CommentRequest r, ClaimsPrincipal u, ResultReviewService s, CancellationToken ct) =>
        { await s.ReviewAsync(id, u.UserName(), r.Comment, ct); return Results.NoContent(); }).RequireAuthorization(Roles.Technologist);
        api.MapPost("/results/{id:guid}/approve", async (Guid id, ApproveRequest r, ClaimsPrincipal u, ResultReviewService s, IClock clock, CancellationToken ct) =>
        {
            if (!SignedInRecently(u, clock)) return Results.Json(new { error = "sign in again to approve" }, statusCode: 403);
            return Results.Ok(new { approvalId = await s.ApproveAsync(id, u.UserName(), r.Critical, ct) });
        }).RequireAuthorization(Roles.Approver);
        api.MapPost("/results/{id:guid}/reject", async (Guid id, ReasonRequest r, ClaimsPrincipal u, ResultReviewService s, CancellationToken ct) =>
        { await s.RejectAsync(id, u.UserName(), r.Reason, ct); return Results.NoContent(); }).RequireAuthorization(Roles.Approver);
        api.MapPost("/results/{id:guid}/correct", async (Guid id, CorrectionRequest r, ClaimsPrincipal u, ResultReviewService s, CancellationToken ct) =>
            Results.Ok(new { resultId = await s.CorrectAsync(id, u.UserName(), r, ct) })).RequireAuthorization(Roles.Approver);

        // ------------------------------------------------------------ exceptions, replay (design 7.4)
        api.MapGet("/exceptions", (ClinicalQueries q, CancellationToken ct) => q.OpenExceptionsAsync(ct));
        api.MapPost("/inbox/{id:guid}/replay", async (Guid id, ReasonRequest r, ClaimsPrincipal u, Db db, InboxStore inbox, AuditWriter audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(r.Reason)) return Results.BadRequest(new { error = "reason required" });
            var n = await db.InTransactionAsync(async (c, t) =>
            {
                var changed = await inbox.RequeueAsync(c, t, id);
                if (changed > 0)
                    await audit.WriteAsync(c, t, new AuditEntry(u.UserName(), "inbox.replayed", "inbox_message", id.ToString(), Reason: r.Reason));
                return changed;
            }, ct);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireAuthorization(Roles.IntegrationOperator);
        api.MapPost("/outbox/{id:guid}/replay", async (Guid id, ReasonRequest r, ClaimsPrincipal u, Db db, OutboxStore outbox, AuditWriter audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(r.Reason)) return Results.BadRequest(new { error = "reason required" });
            var n = await db.InTransactionAsync(async (c, t) =>
            {
                var changed = await outbox.RequeueAsync(c, t, id);
                if (changed > 0)
                    await audit.WriteAsync(c, t, new AuditEntry(u.UserName(), "outbox.replayed", "outbox_message", id.ToString(), Reason: r.Reason));
                return changed;
            }, ct);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireAuthorization(Roles.IntegrationOperator);
        api.MapPost("/links/{instrumentId}/pause", (string instrumentId, LinkHealthRegistry links) =>
        { links.Get(instrumentId).Paused = true; return Results.NoContent(); }).RequireAuthorization(Roles.IntegrationOperator);
        api.MapPost("/links/{instrumentId}/resume", (string instrumentId, LinkHealthRegistry links) =>
        { links.Get(instrumentId).Paused = false; return Results.NoContent(); }).RequireAuthorization(Roles.IntegrationOperator);

        // ------------------------------------------------------------ catalog and mappings (Lab manager)
        api.MapPost("/catalog", async (CatalogEntry e, ClaimsPrincipal u, CatalogService s, CancellationToken ct) =>
            Results.Ok(new { version = await s.PublishAsync(e, u.UserName(), ct) })).RequireAuthorization(Roles.LabManager);
        api.MapPost("/mappings", async (MappingProposal p, ClaimsPrincipal u, MappingService s, CancellationToken ct) =>
            Results.Ok(new { mappingId = await s.ProposeAsync(p, u.UserName(), ct) })).RequireAuthorization(Roles.LabManager);
        api.MapPost("/mappings/{id:guid}/approve", async (Guid id, ClaimsPrincipal u, MappingService s, CancellationToken ct) =>
        { await s.ApproveAsync(id, u.UserName(), ct); return Results.NoContent(); }).RequireAuthorization(Roles.LabManager);

        // ------------------------------------------------------------ audit (Auditor)
        api.MapGet("/audit/verify", async (AuditVerifier v, CancellationToken ct) => Results.Ok(await v.VerifyAsync(ct))).RequireAuthorization(Roles.Auditor);
        api.MapGet("/audit", async (string? accession, string? correlationId, string? entityId, Db db, CancellationToken ct) =>
        {
            await using var conn = await db.OpenAsync(ct);
            return Results.Ok(await conn.QueryAsync("""
                SELECT seq, occurred_at, actor, action, entity_type, entity_id, accession, patient_id, correlation_id, reason,
                       before_json::text AS before, after_json::text AS after
                FROM audit.audit_event
                WHERE (@accession::text IS NULL OR accession=@accession)
                  AND (@correlationId::text IS NULL OR correlation_id=@correlationId)
                  AND (@entityId::text IS NULL OR entity_id=@entityId)
                ORDER BY seq DESC LIMIT 1000
                """, new { accession, correlationId, entityId }));
        }).RequireAuthorization(Roles.Auditor);

        // ------------------------------------------------------------ users (Administrator)
        api.MapPost("/users", async (CreateUserRequest r, ClaimsPrincipal u, UserStore users, CancellationToken ct) =>
            Results.Ok(new { id = await users.CreateAsync(r.Username, r.DisplayName, r.Password, r.Roles, u.UserName(), ct) }))
            .RequireAuthorization(Roles.Administrator);

        // Workflow refusals become 409 with the rule text.
        app.Use(async (ctx, next) =>
        {
            try { await next(ctx); }
            catch (WorkflowException ex) when (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 409;
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });
    }

    public static bool SignedInRecently(ClaimsPrincipal u, IClock clock) =>
        long.TryParse(u.FindFirstValue("auth_time"), out var t) && clock.UtcNow - DateTimeOffset.FromUnixTimeSeconds(t) <= ApprovalReauthWindow;
}
