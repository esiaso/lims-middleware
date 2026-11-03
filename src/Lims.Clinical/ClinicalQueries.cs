using Dapper;
using Lims.Platform;

namespace Lims.Clinical;

public sealed class ReviewQueueRow
{
    public Guid ResultId { get; init; }
    public int Version { get; init; }
    public string Status { get; init; } = default!;
    public string Accession { get; init; } = default!;
    public string Mrn { get; init; } = default!;
    public string? PatientName { get; init; }
    public string TestCode { get; init; } = default!;
    public string ValueText { get; init; } = default!;
    public string? Units { get; init; }
    public string? ReferenceRange { get; init; }
    public string? AbnormalFlag { get; init; }
    public string[] InstrumentFlags { get; init; } = default!;
    public bool FlagMismatch { get; init; }
    public bool IsCritical { get; init; }
    public bool DeltaFlag { get; init; }
    public decimal? DeltaPreviousValue { get; init; }
    public string Priority { get; init; } = default!;
    public DateTime CreatedAt { get; init; }
    public string? ChangeReason { get; init; }
}

public sealed class ExceptionRow
{
    public Guid InboxMessageId { get; init; }
    public Guid Id { get; init; }
    public string Kind { get; init; } = default!;
    public string InstrumentId { get; init; } = default!;
    public string? SampleId { get; init; }
    public string TestCode { get; init; } = default!;
    public string RawValue { get; init; } = default!;
    public string? Reason { get; init; }
    public DateTime ReceivedAt { get; init; }
}

public sealed class DeliveryRow
{
    public Guid ResultId { get; init; }
    public string Accession { get; init; } = default!;
    public string TestCode { get; init; } = default!;
    public string ValueText { get; init; } = default!;
    public string DeliveryState { get; init; } = default!;
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public string? HmisRef { get; init; }
    public DateTime ApprovedAt { get; init; }
}

public sealed class OrderRow
{
    public Guid OrderId { get; init; }
    public string HmisOrderId { get; init; } = default!;
    public string Mrn { get; init; } = default!;
    public string? PatientName { get; init; }
    public string Status { get; init; } = default!;
    public string Priority { get; init; } = default!;
    public bool IsDowntime { get; init; }
    public string? LinkedHmisOrderId { get; init; }
    public string Tests { get; init; } = default!;
    public DateTime CreatedAt { get; init; }
}

public sealed class ClinicalQueries(Db db)
{
    public async Task<IReadOnlyList<ReviewQueueRow>> ReviewQueueAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<ReviewQueueRow>("""
            SELECT r.id AS ResultId, r.version, r.status, s.accession, p.mrn, concat_ws(', ', p.family_name, p.given_name) AS PatientName,
                   ot.catalog_code AS TestCode, r.value_text AS ValueText, r.units, r.reference_range AS ReferenceRange,
                   r.abnormal_flag AS AbnormalFlag, r.instrument_flags AS InstrumentFlags, r.flag_mismatch AS FlagMismatch,
                   r.is_critical AS IsCritical, r.delta_flag AS DeltaFlag, r.delta_previous_value AS DeltaPreviousValue, o.priority, r.created_at AS CreatedAt, r.change_reason AS ChangeReason
            FROM clinical.result r
            JOIN clinical.specimen_test st ON st.id=r.specimen_test_id
            JOIN clinical.specimen s ON s.id=st.specimen_id
            JOIN clinical.order_test ot ON ot.id=st.order_test_id
            JOIN clinical.lab_order o ON o.id=ot.order_id
            JOIN clinical.patient p ON p.id=o.patient_id
            WHERE r.status IN ('received','reviewed')
            ORDER BY r.is_critical DESC, (o.priority='stat') DESC, r.created_at
            LIMIT 500
            """)).ToList();
    }

    public async Task<IReadOnlyList<ExceptionRow>> OpenExceptionsAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<ExceptionRow>("""
            SELECT id, inbox_message_id AS InboxMessageId, disposition AS Kind, instrument_id AS InstrumentId, sample_id AS SampleId, instrument_test_code AS TestCode,
                   raw_value AS RawValue, quarantine_reason AS Reason, received_at AS ReceivedAt
            FROM clinical.instrument_result
            WHERE disposition IN ('unmatched','quarantined') AND resolved_at IS NULL
            ORDER BY received_at DESC LIMIT 500
            """)).ToList();
    }

    /// <summary>Delivery state vocabulary from design section 8 (Approved, Queued, Sent, HMIS-confirmed, Failed).</summary>
    public async Task<IReadOnlyList<DeliveryRow>> DeliveriesAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<DeliveryRow>("""
            SELECT r.id AS ResultId, s.accession, ot.catalog_code AS TestCode, r.value_text AS ValueText,
                   CASE WHEN m.id IS NULL THEN 'Approved'
                        WHEN m.status='confirmed' THEN 'HMIS-confirmed'
                        WHEN m.status IN ('dead','manual_confirmation') THEN 'Failed'
                        WHEN m.status='sent_unknown' OR m.attempts > 0 THEN 'Sent'
                        ELSE 'Queued' END AS DeliveryState,
                   COALESCE(m.attempts,0) AS Attempts, m.last_error AS LastError, m.external_ref AS HmisRef, rv.at AS ApprovedAt
            FROM clinical.result r
            JOIN clinical.review rv ON rv.result_id=r.id AND rv.action='approve'
            JOIN clinical.specimen_test st ON st.id=r.specimen_test_id
            JOIN clinical.specimen s ON s.id=st.specimen_id
            JOIN clinical.order_test ot ON ot.id=st.order_test_id
            LEFT JOIN messaging.outbox_message m ON m.aggregate_id=r.id
            ORDER BY rv.at DESC LIMIT 500
            """)).ToList();
    }

    public async Task<IReadOnlyList<OrderRow>> OrdersAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<OrderRow>("""
            SELECT o.id AS OrderId, o.hmis_order_id AS HmisOrderId, p.mrn, concat_ws(', ', p.family_name, p.given_name) AS PatientName,
                   o.status, o.priority, o.is_downtime AS IsDowntime, o.linked_hmis_order_id AS LinkedHmisOrderId,
                   COALESCE((SELECT string_agg(ot.catalog_code || ':' || ot.status, ', ' ORDER BY ot.catalog_code) FROM clinical.order_test ot WHERE ot.order_id=o.id), '') AS Tests,
                   o.created_at AS CreatedAt
            FROM clinical.lab_order o JOIN clinical.patient p ON p.id=o.patient_id
            ORDER BY o.created_at DESC LIMIT 200
            """)).ToList();
    }

    /// <summary>An approver closes an exception with a reason (for example: tube re-run, result entered manually).</summary>
    public Task<int> ResolveExceptionAsync(Guid id, string user, string reason, Lims.Audit.AuditWriter audit, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new WorkflowException("A reason is required");
            var n = await conn.ExecuteAsync("""
                UPDATE clinical.instrument_result SET resolved_at=now(), resolved_by=@user
                WHERE id=@id AND disposition IN ('unmatched','quarantined') AND resolved_at IS NULL
                """, new { id, user }, tx);
            if (n > 0) await audit.WriteAsync(conn, tx, new Lims.Audit.AuditEntry(user, "exception.resolved", "instrument_result", id.ToString(), Reason: reason));
            return n;
        }, ct);
}
