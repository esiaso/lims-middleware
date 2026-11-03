using Dapper;
using Lims.Audit;
using Lims.Contracts;
using Lims.Messaging;
using Lims.Platform;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lims.Clinical;

public sealed class ReviewOptions
{
    /// <summary>When false, the user who reviewed a result cannot also approve it (design 9.1, configurable).</summary>
    public bool AllowApproveOwnReview { get; set; }
}

public sealed record CriticalNotification(string NotifiedPerson, string Method, DateTimeOffset NotifiedAt, bool ReadBackDone);

public sealed record CorrectionRequest(string ValueText, decimal? NumericValue, string? Units, string Reason);

/// <summary>
/// Review, approval, rejection and correction (design 7.8). Approval writes the result state, the review
/// row, the outbox message and the audit event in ONE transaction; the database refuses an outbox row
/// without an approval (AC4).
/// </summary>
public sealed class ResultReviewService(Db db, AuditWriter audit, OutboxStore outbox, IClock clock, IOptions<ReviewOptions> options)
{
    private sealed class ResultCtx
{
    public Guid Id { get; init; }
    public Guid ResultGroup { get; init; }
    public int Version { get; init; }
    public string Status { get; init; } = default!;
    public bool IsCritical { get; init; }
    public bool DeltaFlag { get; init; }
    public string ValueType { get; init; } = default!;
    public string ValueText { get; init; } = default!;
    public string? Comparator { get; init; }
    public string? Units { get; init; }
    public string? ReferenceRange { get; init; }
    public string? AbnormalFlag { get; init; }
    public Guid SpecimenTestId { get; init; }
    public string Accession { get; init; } = default!;
    public string Mrn { get; init; } = default!;
    public string HmisOrderId { get; init; } = default!;
    public string HmisLineId { get; init; } = default!;
    public string? HmisEncounterId { get; init; }
    public string CatalogCode { get; init; } = default!;
    public string? Loinc { get; init; }
    public string Priority { get; init; } = default!;
    public bool IsDowntime { get; init; }
    public string? LinkedHmisOrderId { get; init; }
    public DateTime? RunAt { get; init; }
    public string? InstrumentId { get; init; }
    public Guid? SupersedesId { get; init; }
    public string? ChangeReason { get; init; }
}

    private static async Task<ResultCtx> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid resultId)
    {
        var ctx = await conn.QuerySingleOrDefaultAsync<ResultCtx>("""
            SELECT r.id, r.result_group AS ResultGroup, r.version, r.status, r.is_critical AS IsCritical, r.delta_flag AS DeltaFlag, r.value_type AS ValueType,
                   r.value_text AS ValueText, r.comparator, r.units, r.reference_range AS ReferenceRange, r.abnormal_flag AS AbnormalFlag,
                   r.specimen_test_id AS SpecimenTestId, s.accession, p.mrn, o.hmis_order_id AS HmisOrderId, ot.hmis_line_id AS HmisLineId,
                   e.hmis_encounter_id AS HmisEncounterId, ot.catalog_code AS CatalogCode, COALESCE(ot.loinc, cat.loinc) AS Loinc, o.priority,
                   o.is_downtime AS IsDowntime, o.linked_hmis_order_id AS LinkedHmisOrderId, ir.run_at AS RunAt, ir.instrument_id AS InstrumentId,
                   r.supersedes_id AS SupersedesId, r.change_reason AS ChangeReason
            FROM clinical.result r
            JOIN clinical.specimen_test st ON st.id = r.specimen_test_id
            JOIN clinical.specimen s ON s.id = st.specimen_id
            JOIN clinical.order_test ot ON ot.id = st.order_test_id
            JOIN clinical.lab_order o ON o.id = ot.order_id
            JOIN clinical.patient p ON p.id = o.patient_id
            LEFT JOIN clinical.encounter e ON e.id = o.encounter_id
            LEFT JOIN clinical.instrument_result ir ON ir.id = r.instrument_result_id
            LEFT JOIN clinical.test_catalog cat ON cat.code = ot.catalog_code AND cat.status = 'active'
            WHERE r.id = @resultId
            FOR UPDATE OF r
            """, new { resultId }, tx);
        return ctx ?? throw new WorkflowException("Result not found");
    }

    public Task ReviewAsync(Guid resultId, string user, string? comment, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var r = await LoadAsync(conn, tx, resultId);
            if (r.Status != "received") throw new WorkflowException($"Only received results can be reviewed (status is {r.Status})");
            await conn.ExecuteAsync("INSERT INTO clinical.review (id, result_id, action, actor, reason) VALUES (@id, @resultId, 'review', @user, @comment)",
                new { id = Guid.CreateVersion7(), resultId, user, comment }, tx);
            await conn.ExecuteAsync("UPDATE clinical.result SET status='reviewed' WHERE id=@resultId", new { resultId }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "result.reviewed", "result", resultId.ToString(),
                Accession: r.Accession, PatientId: r.Mrn, Reason: comment));
        }, ct);

    /// <param name="deltaComment">For a delta-flagged result: the approver's confirmation, recorded as a review comment.</param>
    public Task<Guid> ApproveAsync(Guid resultId, string user, CriticalNotification? critical, CancellationToken ct, string? deltaComment = null) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var r = await LoadAsync(conn, tx, resultId);
            if (r.Status is not ("received" or "reviewed")) throw new WorkflowException($"Result cannot be approved from status {r.Status}");
            if (!options.Value.AllowApproveOwnReview)
            {
                var reviewedBySelf = await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM clinical.review WHERE result_id=@resultId AND action='review' AND actor=@user)", new { resultId, user }, tx);
                if (reviewedBySelf) throw new WorkflowException("The reviewer of a result cannot also approve it");
            }
            if (r.IsCritical)
            {
                if (critical is null || string.IsNullOrWhiteSpace(critical.NotifiedPerson) || string.IsNullOrWhiteSpace(critical.Method) || !critical.ReadBackDone)
                    throw new WorkflowException("Critical result: record who was notified, how, when, and that read-back was done");
            }
            if (r.DeltaFlag)
            {
                if (!string.IsNullOrWhiteSpace(deltaComment))
                    await conn.ExecuteAsync("INSERT INTO clinical.review (id, result_id, action, actor, reason) VALUES (@id, @resultId, 'comment', @user, @deltaComment)",
                        new { id = Guid.CreateVersion7(), resultId, user, deltaComment }, tx);
                var confirmed = await conn.ExecuteScalarAsync<bool>("""
                    SELECT EXISTS(SELECT 1 FROM clinical.review WHERE result_id=@resultId AND action IN ('review','comment')
                                  AND length(btrim(COALESCE(reason,''))) > 0)
                    """, new { resultId }, tx);
                if (!confirmed) throw new WorkflowException("Delta check failed: add a review comment confirming the change from the previous result before approving");
            }

            var approvalId = Guid.CreateVersion7();
            await conn.ExecuteAsync("""
                INSERT INTO clinical.review (id, result_id, action, actor, critical_notified_to, critical_method, critical_notified_at, critical_read_back)
                VALUES (@approvalId, @resultId, 'approve', @user, @to, @method, @at, @rb)
                """, new { approvalId, resultId, user, to = critical?.NotifiedPerson, method = critical?.Method, at = critical?.NotifiedAt.UtcDateTime, rb = critical?.ReadBackDone }, tx);
            await conn.ExecuteAsync("UPDATE clinical.result SET status='approved' WHERE id=@resultId", new { resultId }, tx);
            await conn.ExecuteAsync("UPDATE clinical.order_test SET status='released' WHERE id=(SELECT order_test_id FROM clinical.specimen_test WHERE id=@st)",
                new { st = r.SpecimenTestId }, tx);

            var held = r.IsDowntime && r.LinkedHmisOrderId is null;
            if (!held) await EnqueueDeliveryAsync(conn, tx, r, approvalId, user);

            await audit.WriteAsync(conn, tx, new AuditEntry(user, "result.approved", "result", resultId.ToString(),
                Accession: r.Accession, PatientId: r.Mrn,
                After: new { r.ValueText, r.Units, r.Version, critical = critical is null ? null : new { critical.NotifiedPerson, critical.Method, critical.NotifiedAt }, held_for_downtime_link = held }));
            return approvalId;
        }, ct);

    private async Task EnqueueDeliveryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ResultCtx r, Guid approvalId, string approver)
    {
        // A version whose earlier version in the same group was released goes as an amendment.
        var previousApproved = await conn.ExecuteScalarAsync<Guid?>("""
            SELECT r.id FROM clinical.result r
            WHERE r.result_group=@g AND r.version < @v
              AND EXISTS (SELECT 1 FROM clinical.review rv WHERE rv.result_id = r.id AND rv.action='approve')
            ORDER BY r.version DESC LIMIT 1
            """, new { g = r.ResultGroup, v = r.Version }, tx);

        var item = new HmisResultItem(
            MiddlewareResultId: r.Id, Version: r.Version,
            OrderId: r.LinkedHmisOrderId ?? r.HmisOrderId, OrderLineId: r.HmisLineId, EncounterId: r.HmisEncounterId ?? "",
            Mrn: r.Mrn, Accession: r.Accession, TestCode: r.CatalogCode, Loinc: r.Loinc,
            Value: r.ValueText, ValueType: r.ValueType == "qualitative" ? "coded" : r.ValueType, Comparator: r.Comparator,
            Unit: r.Units, ReferenceRange: r.ReferenceRange, AbnormalFlag: r.AbnormalFlag,
            Status: previousApproved is null ? "final" : "amended",
            PerformedAt: r.RunAt is { } run ? new DateTimeOffset(DateTime.SpecifyKind(run, DateTimeKind.Utc)) : null,
            ApprovedAt: clock.UtcNow, ApprovedBy: approver, Instrument: r.InstrumentId, Comment: null);
        var payload = new ResultDeliveryPayload(item, previousApproved, previousApproved is null ? null : r.ChangeReason);
        // Idempotency key = result id + version: a resend of this version always carries the same key (design 6.8).
        var key = $"lims-result-{r.Id:N}-v{r.Version}";
        await outbox.EnqueueAsync(conn, tx, "hmis", previousApproved is null ? OutboxKinds.HmisResult : OutboxKinds.HmisAmendment,
            r.Id, approvalId, Json.Serialize(payload), key, r.Id.ToString(), r.Priority == "stat" ? 10 : 0);
    }

    /// <summary>Reject a result and request a rerun; the test goes back to pending at instrument.</summary>
    public Task RejectAsync(Guid resultId, string user, string reason, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new WorkflowException("A reason is required");
            var r = await LoadAsync(conn, tx, resultId);
            if (r.Status is not ("received" or "reviewed")) throw new WorkflowException($"Result cannot be rejected from status {r.Status}");
            await conn.ExecuteAsync("INSERT INTO clinical.review (id, result_id, action, actor, reason) VALUES (@id, @resultId, 'reject', @user, @reason)",
                new { id = Guid.CreateVersion7(), resultId, user, reason }, tx);
            await conn.ExecuteAsync("UPDATE clinical.result SET status='rejected' WHERE id=@resultId", new { resultId }, tx);
            await conn.ExecuteAsync("UPDATE clinical.order_test SET status='pending_at_instrument' WHERE id=(SELECT order_test_id FROM clinical.specimen_test WHERE id=@st)",
                new { st = r.SpecimenTestId }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "result.rejected", "result", resultId.ToString(), Accession: r.Accession, PatientId: r.Mrn, Reason: reason));
        }, ct);

    /// <summary>Correct a released result: creates version n+1 (needs its own approval; sent as an amendment).</summary>
    public Task<Guid> CorrectAsync(Guid resultId, string user, CorrectionRequest c, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(c.Reason)) throw new WorkflowException("A correction needs a reason");
            var r = await LoadAsync(conn, tx, resultId);
            if (r.Status != "approved") throw new WorkflowException("Only an approved result can be corrected");
            var newer = await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM clinical.result WHERE result_group=@g AND version>@v)",
                new { g = r.ResultGroup, v = r.Version }, tx);
            if (newer) throw new WorkflowException("A newer version of this result already exists");
            var id = Guid.CreateVersion7();
            await conn.ExecuteAsync("""
                INSERT INTO clinical.result (id, result_group, version, specimen_test_id, instrument_result_id, value_type, value_text, numeric_value,
                    comparator, units, reference_range, abnormal_flag, instrument_flags, flag_mismatch, is_critical, status, supersedes_id, change_reason, mapping_id)
                SELECT @id, result_group, version + 1, specimen_test_id, instrument_result_id, value_type, @ValueText, @NumericValue,
                    NULL, COALESCE(@Units, units), reference_range, NULL, instrument_flags, false, is_critical, 'received', id, @Reason, mapping_id
                FROM clinical.result WHERE id=@resultId
                """, new { id, resultId, c.ValueText, c.NumericValue, c.Units, c.Reason }, tx);
            await DeltaCheck.ApplyAsync(conn, tx, id);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "result.correction_created", "result", id.ToString(),
                Accession: r.Accession, PatientId: r.Mrn, Reason: c.Reason,
                Before: new { r.ValueText, r.Version }, After: new { c.ValueText, version = r.Version + 1 }));
            return id;
        }, ct);

    /// <summary>Approve on a downtime order is held until the DT- order is linked to its real HMIS order (design 5.5).</summary>
    public Task<int> LinkDowntimeOrderAsync(Guid orderId, string hmisOrderId, string user, string reason, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var updated = await conn.ExecuteAsync("""
                UPDATE clinical.lab_order SET linked_hmis_order_id=@hmisOrderId WHERE id=@orderId AND is_downtime AND linked_hmis_order_id IS NULL
                """, new { orderId, hmisOrderId }, tx);
            if (updated == 0) throw new WorkflowException("Not an unlinked downtime order");
            var held = (await conn.QueryAsync<(Guid ResultId, Guid ApprovalId, string Approver)>("""
                SELECT r.id, rv.id, rv.actor FROM clinical.result r
                JOIN clinical.review rv ON rv.result_id=r.id AND rv.action='approve'
                JOIN clinical.specimen_test st ON st.id=r.specimen_test_id
                JOIN clinical.order_test ot ON ot.id=st.order_test_id
                WHERE ot.order_id=@orderId AND r.status='approved'
                  AND NOT EXISTS (SELECT 1 FROM messaging.outbox_message m WHERE m.aggregate_id=r.id)
                """, new { orderId }, tx)).ToList();
            foreach (var h in held)
            {
                var r = await LoadAsync(conn, tx, h.ResultId);
                await EnqueueDeliveryAsync(conn, tx, r, h.ApprovalId, h.Approver);
            }
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "order.downtime_linked", "lab_order", orderId.ToString(), Reason: reason,
                After: new { hmisOrderId, released = held.Count }));
            return held.Count;
        }, ct);
}
