using Dapper;
using Lims.Audit;
using Lims.Contracts;
using Lims.Platform;

namespace Lims.Clinical;

public sealed record DowntimeOrderRequest(string Mrn, string? GivenName, string? FamilyName, string? Sex, DateOnly? BirthDate,
    IReadOnlyList<string> TestCodes, string Priority);

/// <summary>HMIS downtime registration with a DT- order number (design 5.5). Results are held until linked.</summary>
public sealed class DowntimeService(Db db, OrderRegistrationHandler registration, AuditWriter audit, IClock clock)
{
    public Task<(Guid OrderId, string DowntimeOrderId)> RegisterAsync(DowntimeOrderRequest req, string user, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            if (req.TestCodes.Count == 0) throw new WorkflowException("At least one test is required");
            var now = clock.UtcNow;
            var dtId = $"DT-{now:yyMMddHHmmss}-{Random.Shared.Next(1000, 9999)}";
            var order = new HmisOrder(dtId, now, "active", req.Priority,
                new HmisPatient(req.Mrn, req.GivenName, req.FamilyName, req.Sex, req.BirthDate, null, null),
                new HmisEncounter(dtId, "downtime", null), null,
                req.TestCodes.Select((c, i) => new HmisOrderLine($"{dtId}-{i + 1}", c, null, c, "active")).ToList());
            await registration.RegisterAsync(conn, tx, order, dtId, user);
            var orderId = await conn.ExecuteScalarAsync<Guid>(
                "UPDATE clinical.lab_order SET is_downtime=true WHERE hmis_order_id=@dtId RETURNING id", new { dtId }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "order.downtime_registered", "lab_order", orderId.ToString(), PatientId: req.Mrn,
                After: new { dtId, req.TestCodes }));
            return (orderId, dtId);
        }, ct);
}
