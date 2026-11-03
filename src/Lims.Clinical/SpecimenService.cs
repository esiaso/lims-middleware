using Dapper;
using Lims.Audit;
using Lims.Platform;
using Npgsql;

namespace Lims.Clinical;

public sealed record SpecimenCreated(Guid SpecimenId, string Accession, string Barcode, string SpecimenType, IReadOnlyList<string> Tests, string Zpl);

public static class RejectionReasons
{
    public static readonly string[] All = ["haemolysed", "clotted", "insufficient_volume", "wrong_container", "unlabelled", "mismatch"];
}

/// <summary>Collection, accessioning, labels, acceptance and rejection (design 5.4, 5.5).</summary>
public sealed class SpecimenService(Db db, AuditWriter audit, IClock clock)
{
    /// <summary>
    /// Creates one specimen per specimen type for the order's open tests and issues accessions.
    /// Calling it again for tests that already have a specimen returns the existing specimen (no duplicate tube).
    /// </summary>
    public Task<IReadOnlyList<SpecimenCreated>> CollectAsync(Guid orderId, string user, CancellationToken ct) =>
        db.InTransactionAsync<IReadOnlyList<SpecimenCreated>>(async (conn, tx) =>
        {
            var order = await conn.QuerySingleOrDefaultAsync<(string Status, string Mrn, string? Given, string? Family, DateTime? Dob)?>("""
                SELECT o.status, p.mrn, p.given_name, p.family_name, p.birth_date::timestamp
                FROM clinical.lab_order o JOIN clinical.patient p ON p.id=o.patient_id WHERE o.id=@orderId FOR UPDATE OF o
                """, new { orderId }, tx) ?? throw new WorkflowException("Order not found");
            if (order.Status == "cancelled") throw new WorkflowException("Order is cancelled");

            var tests = (await conn.QueryAsync<(Guid OrderTestId, string Code, string SpecimenType, string? Instrument, Guid? SpecimenId)>("""
                SELECT ot.id, ot.catalog_code, COALESCE(c.specimen_type, 'serum'), c.default_instrument,
                       (SELECT s.id FROM clinical.specimen_test st JOIN clinical.specimen s ON s.id = st.specimen_id
                        WHERE st.order_test_id = ot.id AND s.status NOT IN ('rejected','recollection_requested') LIMIT 1)
                FROM clinical.order_test ot
                LEFT JOIN clinical.test_catalog c ON c.code = ot.catalog_code AND c.status='active'
                WHERE ot.order_id=@orderId AND ot.status NOT IN ('cancelled','not_performed')
                """, new { orderId }, tx)).ToList();

            var created = new List<SpecimenCreated>();
            var now = clock.UtcNow;
            foreach (var group in tests.Where(t => t.SpecimenId is null).GroupBy(t => t.SpecimenType))
            {
                var (accession, barcode) = await NextAccessionAsync(conn, tx, DateOnly.FromDateTime(now.UtcDateTime.AddHours(3)));
                var specimenId = Guid.CreateVersion7();
                await conn.ExecuteAsync("""
                    INSERT INTO clinical.specimen (id, accession, barcode, order_id, specimen_type, status, collected_at, collected_by, recollection_of)
                    VALUES (@specimenId, @accession, @barcode, @orderId, @type, 'collected', @now, @user,
                        (SELECT id FROM clinical.specimen WHERE order_id=@orderId AND specimen_type=@type AND status='recollection_requested'
                         AND NOT EXISTS (SELECT 1 FROM clinical.specimen n WHERE n.recollection_of = clinical.specimen.id)
                         ORDER BY created_at DESC LIMIT 1))
                    """, new { specimenId, accession, barcode, orderId, type = group.Key, now = now.UtcDateTime, user }, tx);
                foreach (var t in group)
                {
                    await conn.ExecuteAsync("""
                        INSERT INTO clinical.specimen_test (id, specimen_id, order_test_id, instrument_id) VALUES (@id, @specimenId, @ot, @inst)
                        """, new { id = Guid.CreateVersion7(), specimenId, ot = t.OrderTestId, inst = t.Instrument }, tx);
                }
                await conn.ExecuteAsync("UPDATE clinical.lab_order SET status='in_progress' WHERE id=@orderId AND status='registered'", new { orderId }, tx);
                await audit.WriteAsync(conn, tx, new AuditEntry(user, "specimen.collected", "specimen", specimenId.ToString(),
                    Accession: accession, PatientId: order.Mrn, After: new { accession, type = group.Key, tests = group.Select(g => g.Code) }));
                var codes = group.Select(g => g.Code).ToList();
                var zpl = ZplLabel.Render(new LabelData(barcode, accession, $"{order.Family}, {order.Given}", order.Mrn,
                    order.Dob is { } d ? DateOnly.FromDateTime(d) : null, group.Key, string.Join(",", codes), now));
                created.Add(new SpecimenCreated(specimenId, accession, barcode, group.Key, codes, zpl));
            }
            return created;
        }, ct);

    private static async Task<(string Accession, string Barcode)> NextAccessionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, DateOnly day)
    {
        var seq = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO clinical.accession_counter (day, last) VALUES (@day, 1)
            ON CONFLICT (day) DO UPDATE SET last = clinical.accession_counter.last + 1
            RETURNING last
            """, new { day = day.ToDateTime(TimeOnly.MinValue) }, tx);
        return AccessionNumber.Format(day, seq);
    }

    /// <summary>Lab reception accepts the tube (two-identifier check done by the technologist at the bench).</summary>
    public Task AcceptAsync(string accessionOrBarcode, string user, CancellationToken ct) =>
        TransitionAsync(accessionOrBarcode, user, "accepted", null, ct);

    public Task RejectAsync(string accessionOrBarcode, string user, string reason, CancellationToken ct)
    {
        if (!RejectionReasons.All.Contains(reason)) throw new WorkflowException($"Rejection reason must be one of: {string.Join(", ", RejectionReasons.All)}");
        return TransitionAsync(accessionOrBarcode, user, "rejected", reason, ct);
    }

    private Task TransitionAsync(string id, string user, string to, string? reason, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var barcode = AccessionNumber.NormalizeBarcode(id) ?? throw new WorkflowException("Not a valid accession (check digit)");
            var s = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Status, string Accession)?>(
                "SELECT id, status, accession FROM clinical.specimen WHERE barcode=@barcode FOR UPDATE", new { barcode }, tx)
                ?? throw new WorkflowException("Specimen not found");
            var allowed = to switch
            {
                "accepted" => s.Status is "collected" or "received",
                "rejected" => s.Status is "collected" or "received" or "accepted",
                _ => false,
            };
            if (!allowed) throw new WorkflowException($"Cannot change specimen from {s.Status} to {to}");
            await conn.ExecuteAsync("""
                UPDATE clinical.specimen SET status=@to, rejection_reason=@reason,
                    received_at=COALESCE(received_at, now()), received_by=COALESCE(received_by, @user)
                WHERE id=@id
                """, new { id = s.Id, to, reason, user }, tx);
            if (to == "accepted")
            {
                await conn.ExecuteAsync("""
                    UPDATE clinical.order_test SET status='pending_at_instrument'
                    WHERE id IN (SELECT order_test_id FROM clinical.specimen_test WHERE specimen_id=@id) AND status='pending_specimen'
                    """, new { id = s.Id }, tx);
            }
            await audit.WriteAsync(conn, tx, new AuditEntry(user, $"specimen.{to}", "specimen", s.Id.ToString(), Accession: s.Accession,
                Reason: reason, Before: new { status = s.Status }, After: new { status = to }));
        }, ct);

    /// <summary>
    /// Asks the ward for a new tube (design 5.4). The old specimen keeps its history; tests without a released result go back to
    /// pending specimen, and the next collection for the order is linked to this one through recollection_of.
    /// </summary>
    public Task RequestRecollectionAsync(string accessionOrBarcode, string user, string reason, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new WorkflowException("A reason is required");
            var barcode = AccessionNumber.NormalizeBarcode(accessionOrBarcode) ?? throw new WorkflowException("Not a valid accession (check digit)");
            var sp = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Status, string Accession)?>(
                "SELECT id, status, accession FROM clinical.specimen WHERE barcode=@barcode FOR UPDATE", new { barcode }, tx)
                ?? throw new WorkflowException("Specimen not found");
            if (sp.Status is not ("collected" or "received" or "accepted" or "rejected" or "on_instrument"))
                throw new WorkflowException($"Cannot request recollection for a specimen that is {sp.Status}");
            await conn.ExecuteAsync("UPDATE clinical.specimen SET status='recollection_requested' WHERE id=@id", new { id = sp.Id }, tx);
            await conn.ExecuteAsync("""
                UPDATE clinical.order_test SET status='pending_specimen'
                WHERE id IN (SELECT order_test_id FROM clinical.specimen_test WHERE specimen_id=@id)
                  AND status IN ('ordered','pending_specimen','pending_at_instrument','resulted')
                  AND NOT EXISTS (SELECT 1 FROM clinical.specimen_test st JOIN clinical.result r ON r.specimen_test_id=st.id
                                  WHERE st.order_test_id=clinical.order_test.id AND r.status='approved')
                """, new { id = sp.Id }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "specimen.recollection_requested", "specimen", sp.Id.ToString(), Accession: sp.Accession,
                Reason: reason, Before: new { status = sp.Status }, After: new { status = "recollection_requested" }));
        }, ct);

    /// <summary>Reprint reuses the same accession and is audited (design 5.5).</summary>
    public Task<string> ReprintLabelAsync(string accessionOrBarcode, string user, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var barcode = AccessionNumber.NormalizeBarcode(accessionOrBarcode) ?? throw new WorkflowException("Not a valid accession");
            var r = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Accession, string Type, DateTime? Collected, string Mrn, string? Given, string? Family, DateTime? Dob)?>("""
                SELECT s.id, s.accession, s.specimen_type, s.collected_at, p.mrn, p.given_name, p.family_name, p.birth_date::timestamp
                FROM clinical.specimen s JOIN clinical.lab_order o ON o.id=s.order_id JOIN clinical.patient p ON p.id=o.patient_id
                WHERE s.barcode=@barcode
                """, new { barcode }, tx) ?? throw new WorkflowException("Specimen not found");
            var codes = await conn.QueryAsync<string>("""
                SELECT ot.catalog_code FROM clinical.specimen_test st JOIN clinical.order_test ot ON ot.id=st.order_test_id WHERE st.specimen_id=@id
                """, new { id = r.Id }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(user, "specimen.label_reprinted", "specimen", r.Id.ToString(), Accession: r.Accession, PatientId: r.Mrn));
            return ZplLabel.Render(new LabelData(barcode, r.Accession, $"{r.Family}, {r.Given}", r.Mrn,
                r.Dob is { } d ? DateOnly.FromDateTime(d) : null, r.Type, string.Join(",", codes),
                r.Collected is { } c ? new DateTimeOffset(DateTime.SpecifyKind(c, DateTimeKind.Utc)) : clock.UtcNow));
        }, ct);
}
