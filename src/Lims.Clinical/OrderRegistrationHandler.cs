using System.Text;
using Dapper;
using Lims.Audit;
using Lims.Contracts;
using Lims.Messaging;
using Lims.Platform;
using Npgsql;

namespace Lims.Clinical;

/// <summary>
/// Registers HMIS orders from the inbox (design 7.1 step 2). Upserts by HMIS IDs, so re-delivery is a
/// no-op; the HMIS wins on patient demographics. Cancellation after release raises a task, never a silent delete.
/// </summary>
public sealed class OrderRegistrationHandler(AuditWriter audit) : IInboxHandler
{
    public bool CanHandle(string source, string kind) => source == MessageSources.Hmis && kind == InboxKinds.HmisOrder;

    public async Task<InboxResult> HandleAsync(InboxItem item, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        HmisOrder order;
        try { order = Json.Deserialize<HmisOrder>(Encoding.UTF8.GetString(item.Raw)); }
        catch (Exception ex) { throw new PermanentMessageException($"Invalid HMIS order JSON: {ex.Message}"); }
        if (string.IsNullOrWhiteSpace(order.Patient?.Mrn)) throw new PermanentMessageException("HMIS order has no patient MRN");
        return await RegisterAsync(conn, tx, order, item.CorrelationId, "system:hmis-intake");
    }

    public async Task<InboxResult> RegisterAsync(NpgsqlConnection conn, NpgsqlTransaction tx, HmisOrder order, string correlationId, string actor)
    {
        var patientId = await UpsertPatientAsync(conn, tx, order.Patient, correlationId);
        var encounterId = await conn.ExecuteScalarAsync<Guid>("""
            INSERT INTO clinical.encounter (id, hmis_encounter_id, patient_id, type, location)
            VALUES (@id, @hid, @patientId, @type, @location)
            ON CONFLICT (hmis_encounter_id) DO UPDATE SET type=EXCLUDED.type, location=EXCLUDED.location
            RETURNING id
            """, new { id = Guid.CreateVersion7(), hid = order.Encounter.Id, patientId, type = order.Encounter.Type, location = order.Encounter.Location }, tx);

        var existing = await conn.QuerySingleOrDefaultAsync<(Guid Id, DateTime? Updated, string Status)?>(
            "SELECT id, hmis_updated_at, status FROM clinical.lab_order WHERE hmis_order_id=@id FOR UPDATE", new { id = order.Id }, tx);
        if (existing is { } e && e.Updated is { } u && u >= order.UpdatedAt.UtcDateTime)
            return new InboxResult(InboxDisposition.Duplicate, "order version already registered");

        var priority = string.Equals(order.Priority, "stat", StringComparison.OrdinalIgnoreCase) ? "stat" : "routine";
        Guid orderId;
        if (existing is null)
        {
            orderId = Guid.CreateVersion7();
            await conn.ExecuteAsync("""
                INSERT INTO clinical.lab_order (id, hmis_order_id, encounter_id, patient_id, priority, ordering_clinician, status, hmis_updated_at)
                VALUES (@orderId, @hid, @encounterId, @patientId, @priority, @clinician, 'registered', @updated)
                """, new { orderId, hid = order.Id, encounterId, patientId, priority, clinician = order.OrderingClinician, updated = order.UpdatedAt.UtcDateTime }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(actor, "order.registered", "lab_order", orderId.ToString(),
                PatientId: order.Patient.Mrn, CorrelationId: correlationId, After: new { order.Id, priority, lines = order.Lines.Select(l => l.TestCode) }));
        }
        else
        {
            orderId = existing.Value.Id;
            await conn.ExecuteAsync("""
                UPDATE clinical.lab_order SET priority=@priority, ordering_clinician=@clinician, hmis_updated_at=@updated, encounter_id=@encounterId
                WHERE id=@orderId
                """, new { orderId, priority, clinician = order.OrderingClinician, updated = order.UpdatedAt.UtcDateTime, encounterId }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(actor, "order.updated", "lab_order", orderId.ToString(),
                PatientId: order.Patient.Mrn, CorrelationId: correlationId, After: new { order.Id, order.Status, priority }));
        }

        var orderCancelled = string.Equals(order.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
        foreach (var line in order.Lines)
        {
            var lineCancelled = orderCancelled || string.Equals(line.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
            // A panel line has one row per component; all share the HMIS line id.
            var lineRows = (await conn.QueryAsync<(Guid Id, string Status)>(
                "SELECT id, status FROM clinical.order_test WHERE order_id=@orderId AND hmis_line_id=@lid FOR UPDATE",
                new { orderId, lid = line.Id }, tx)).ToList();
            if (lineRows.Count == 0)
            {
                if (lineCancelled) continue;
                var components = await conn.QuerySingleOrDefaultAsync<string[]?>(
                    "SELECT panel_components FROM clinical.test_catalog WHERE code=@code AND status='active'", new { code = line.TestCode }, tx);
                if (components is { Length: > 0 })
                {
                    await conn.ExecuteAsync("""
                        INSERT INTO clinical.order_test (id, order_id, hmis_line_id, catalog_code, loinc, panel_code, status)
                        SELECT gen_random_uuid(), @orderId, @lid, c.code, c.loinc, @panel, 'pending_specimen'
                        FROM unnest(@components::text[]) WITH ORDINALITY AS u(code, ord)
                        JOIN clinical.test_catalog c ON c.code = u.code AND c.status = 'active'
                        ORDER BY u.ord
                        """, new { orderId, lid = line.Id, panel = line.TestCode, components }, tx);
                }
                else
                {
                    await conn.ExecuteAsync("""
                        INSERT INTO clinical.order_test (id, order_id, hmis_line_id, catalog_code, loinc, status)
                        VALUES (@id, @orderId, @lid, @code, @loinc, 'pending_specimen')
                        """, new { id = Guid.CreateVersion7(), orderId, lid = line.Id, code = line.TestCode, loinc = line.Loinc }, tx);
                }
            }
            else if (lineCancelled)
            {
                foreach (var row in lineRows.Where(r => r.Status != "cancelled"))
                {
                    if (row.Status == "released")
                    {
                        // Result already released: an approver must handle it (design 5.4).
                        await audit.WriteAsync(conn, tx, new AuditEntry(actor, "order_test.cancel_after_release", "order_test", row.Id.ToString(),
                            PatientId: order.Patient.Mrn, CorrelationId: correlationId, Reason: "HMIS cancelled a line whose result was already released"));
                    }
                    else
                    {
                        await conn.ExecuteAsync("UPDATE clinical.order_test SET status='cancelled' WHERE id=@id", new { id = row.Id }, tx);
                        await audit.WriteAsync(conn, tx, new AuditEntry(actor, "order_test.cancelled", "order_test", row.Id.ToString(),
                            PatientId: order.Patient.Mrn, CorrelationId: correlationId));
                    }
                }
            }
        }

        if (orderCancelled)
        {
            var released = await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM clinical.order_test WHERE order_id=@orderId AND status='released'", new { orderId }, tx);
            if (released == 0)
                await conn.ExecuteAsync("UPDATE clinical.lab_order SET status='cancelled' WHERE id=@orderId", new { orderId }, tx);
        }
        return new InboxResult(InboxDisposition.Processed);
    }

    private async Task<Guid> UpsertPatientAsync(NpgsqlConnection conn, NpgsqlTransaction tx, HmisPatient p, string correlationId)
    {
        var before = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT id, given_name, family_name, sex, birth_date, national_id, sha_number FROM clinical.patient WHERE mrn=@mrn FOR UPDATE", new { mrn = p.Mrn }, tx);
        var birth = p.BirthDate?.ToDateTime(TimeOnly.MinValue);
        var id = await conn.ExecuteScalarAsync<Guid>("""
            INSERT INTO clinical.patient (id, mrn, given_name, family_name, sex, birth_date, national_id, sha_number, last_synced_at)
            VALUES (@id, @Mrn, @GivenName, @FamilyName, @Sex, @birth, @NationalId, @ShaNumber, now())
            ON CONFLICT (mrn) DO UPDATE SET given_name=EXCLUDED.given_name, family_name=EXCLUDED.family_name, sex=EXCLUDED.sex,
                birth_date=EXCLUDED.birth_date, national_id=EXCLUDED.national_id, sha_number=EXCLUDED.sha_number, last_synced_at=now()
            RETURNING id
            """, new { id = Guid.CreateVersion7(), p.Mrn, p.GivenName, p.FamilyName, p.Sex, birth, p.NationalId, p.ShaNumber }, tx);
        if (before is not null &&
            ((string?)before.given_name != p.GivenName || (string?)before.family_name != p.FamilyName || (string?)before.sex != p.Sex))
        {
            await audit.WriteAsync(conn, tx, new AuditEntry("system:hmis-intake", "patient.demographics_changed", "patient", id.ToString(),
                PatientId: p.Mrn, CorrelationId: correlationId,
                Before: new { given = (string?)before.given_name, family = (string?)before.family_name, sex = (string?)before.sex },
                After: new { given = p.GivenName, family = p.FamilyName, sex = p.Sex }));
        }
        return id;
    }
}
