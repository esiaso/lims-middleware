using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Lims.Audit;
using Lims.Contracts;
using Lims.Messaging;
using Lims.Platform;
using Npgsql;

namespace Lims.Clinical;

/// <summary>
/// Inbox handler for analyzer messages (design 7.1 step 7): parse, de-duplicate, divert QC, match by
/// barcode, apply the active mapping, and create result version 1 in "received". Anything ambiguous is
/// quarantined with a reason; nothing is dropped silently.
/// </summary>
public sealed class InstrumentResultHandler(IInstrumentParserResolver parsers, AuditWriter audit) : IInboxHandler
{
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromDays(7);

    public bool CanHandle(string source, string kind) => source == MessageSources.Instrument && kind is InboxKinds.AstmMessage or InboxKinds.Hl7Message;

    public async Task<InboxResult> HandleAsync(InboxItem item, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var parser = parsers.ForInstrument(item.Channel)
            ?? throw new PermanentMessageException($"No parser configured for instrument {item.Channel}");
        var parsed = parser.Parse(item.Channel, item.Raw, item.CorrelationId);
        if (!parsed.Success)
        {
            await audit.WriteAsync(conn, tx, new AuditEntry("system:inbox", "instrument_message.quarantined", "inbox_message", item.Id.ToString(),
                CorrelationId: item.CorrelationId, Reason: parsed.Error));
            return new InboxResult(InboxDisposition.Quarantined, parsed.Error);
        }

        // On an authorized replay, earlier exceptions from this same message are superseded by this run.
        await conn.ExecuteAsync("""
            UPDATE clinical.instrument_result SET resolved_at=now(), resolved_by='system:replay'
            WHERE inbox_message_id=@id AND disposition IN ('unmatched','quarantined') AND resolved_at IS NULL
            """, new { id = item.Id }, tx);

        var groups = parsed.Messages.Where(m => m.Kind is MessageKind.PatientResult or MessageKind.Qc).ToList();
        if (groups.Count == 0) return new InboxResult(InboxDisposition.Processed, "query or status message");

        var duplicates = 0;
        foreach (var g in groups)
        {
            var fingerprint = Fingerprint(g);
            var seen = await conn.ExecuteScalarAsync<bool>("""
                SELECT EXISTS (SELECT 1 FROM clinical.instrument_result
                               WHERE instrument_id=@i AND fingerprint=@fingerprint AND received_at > now() - @window
                                 AND disposition IN ('matched','duplicate'))
                """, new { i = item.Channel, fingerprint, window = DuplicateWindow }, tx);
            if (seen) { duplicates++; continue; }

            if (g.Kind == MessageKind.Qc) await StoreQcAsync(conn, tx, item, g, fingerprint);
            else await StorePatientResultsAsync(conn, tx, item, g, fingerprint);
        }
        return duplicates == groups.Count
            ? new InboxResult(InboxDisposition.Duplicate, "same content already received")
            : new InboxResult(InboxDisposition.Processed);
    }

    public static string Fingerprint(NormalizedInstrumentMessage g)
    {
        var sb = new StringBuilder();
        sb.Append(g.InstrumentId).Append('|').Append(g.Kind).Append('|').Append(g.SampleId).Append('|');
        foreach (var r in g.Results.OrderBy(r => r.InstrumentTestCode, StringComparer.Ordinal))
            sb.Append(r.InstrumentTestCode).Append('=').Append(r.RawValue).Append('@').Append(r.RunAt?.ToUnixTimeSeconds())
              .Append('/').Append(r.ResultStatus).Append('/').Append(r.Dilution).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private async Task StoreQcAsync(NpgsqlConnection conn, NpgsqlTransaction tx, InboxItem item, NormalizedInstrumentMessage g, string fingerprint)
    {
        // A QC record carrying a patient barcode is suspicious: quarantine rather than file as QC (design 5.5).
        if (AccessionNumber.NormalizeBarcode(g.SampleId) is { } bc &&
            await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM clinical.specimen WHERE barcode=@bc)", new { bc }, tx))
        {
            foreach (var r in g.Results)
                await InsertInstrumentResultAsync(conn, tx, item, g, r, fingerprint, "quarantined", "QC record with a patient barcode", null, null);
            await audit.WriteAsync(conn, tx, new AuditEntry("system:inbox", "qc.quarantined_patient_barcode", "inbox_message", item.Id.ToString(),
                CorrelationId: item.CorrelationId, Accession: g.SampleId));
            return;
        }
        foreach (var r in g.Results)
        {
            await conn.ExecuteAsync("""
                INSERT INTO clinical.qc_result (id, inbox_message_id, instrument_id, control_name, control_lot, instrument_test_code,
                    raw_value, numeric_value, units, flags, run_at)
                VALUES (@id, @inbox, @inst, @name, @lot, @code, @raw, @num, @units, @flags, @run)
                """, new
            {
                id = Guid.CreateVersion7(), inbox = item.Id, inst = g.InstrumentId, name = g.ControlName, lot = g.ControlLot,
                code = r.InstrumentTestCode, raw = r.RawValue, num = r.NumericValue, units = r.Units,
                flags = r.InstrumentFlags.ToArray(), run = r.RunAt?.UtcDateTime,
            }, tx);
        }
        // Fingerprint record so a resent QC message is detected as a duplicate.
        await conn.ExecuteAsync("""
            INSERT INTO clinical.instrument_result (id, inbox_message_id, instrument_id, sample_id, instrument_test_code, raw_value,
                fingerprint, disposition, quarantine_reason, correlation_id)
            VALUES (@id, @inbox, @inst, @sid, '*qc*', '', @fingerprint, 'duplicate', 'qc fingerprint marker', @corr)
            """, new { id = Guid.CreateVersion7(), inbox = item.Id, inst = g.InstrumentId, sid = g.SampleId, fingerprint, corr = item.CorrelationId }, tx);
        await audit.WriteAsync(conn, tx, new AuditEntry("system:inbox", "qc.received", "inbox_message", item.Id.ToString(),
            CorrelationId: item.CorrelationId, After: new { control = g.ControlName, tests = g.Results.Count }));
    }

    private sealed class SpecimenRow
{
    public Guid Id { get; init; }
    public string Accession { get; init; } = default!;
    public string Status { get; init; } = default!;
    public string Mrn { get; init; } = default!;
    public string? Sex { get; init; }
    public DateTime? BirthDate { get; init; }
}

    private sealed class MappingRow
{
    public Guid Id { get; init; }
    public string CatalogCode { get; init; } = default!;
    public string ResultType { get; init; } = default!;
    public decimal UnitFactor { get; init; }
    public string? InstrumentUnit { get; init; }
    public bool Reportable { get; init; }
}

    private sealed class CatalogRow
{
    public string Code { get; init; } = default!;
    public string? UcumUnit { get; init; }
    public int Decimals { get; init; }
    public decimal? RefLow { get; init; }
    public decimal? RefHigh { get; init; }
    public decimal? CriticalLow { get; init; }
    public decimal? CriticalHigh { get; init; }
}

    private async Task StorePatientResultsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, InboxItem item, NormalizedInstrumentMessage g, string fingerprint)
    {
        var barcode = AccessionNumber.NormalizeBarcode(g.SampleId);
        var specimen = barcode is null ? null : await conn.QuerySingleOrDefaultAsync<SpecimenRow>("""
            SELECT s.id, s.accession, s.status, p.mrn, p.sex, p.birth_date::timestamp AS BirthDate
            FROM clinical.specimen s JOIN clinical.lab_order o ON o.id=s.order_id JOIN clinical.patient p ON p.id=o.patient_id
            WHERE s.barcode=@barcode FOR UPDATE OF s
            """, new { barcode }, tx);

        if (specimen is null)
        {
            foreach (var r in g.Results)
                await InsertInstrumentResultAsync(conn, tx, item, g, r, fingerprint, "unmatched", "unknown barcode", null, null);
            await audit.WriteAsync(conn, tx, new AuditEntry("system:inbox", "result.unmatched", "inbox_message", item.Id.ToString(),
                CorrelationId: item.CorrelationId, Reason: $"no specimen for sample id '{g.SampleId}'"));
            return;
        }
        if (specimen.Status is not ("accepted" or "on_instrument" or "testing_complete" or "received"))
        {
            foreach (var r in g.Results)
                await InsertInstrumentResultAsync(conn, tx, item, g, r, fingerprint, "quarantined", $"specimen is {specimen.Status}", null, null);
            await audit.WriteAsync(conn, tx, new AuditEntry("system:inbox", "result.quarantined", "specimen", specimen.Id.ToString(),
                Accession: specimen.Accession, PatientId: specimen.Mrn, CorrelationId: item.CorrelationId, Reason: $"specimen is {specimen.Status}"));
            return;
        }

        foreach (var r in g.Results)
        {
            var mapping = await conn.QuerySingleOrDefaultAsync<MappingRow>("""
                SELECT id, catalog_code AS CatalogCode, result_type AS ResultType, unit_factor AS UnitFactor,
                       instrument_unit AS InstrumentUnit, reportable
                FROM instruments.test_mapping WHERE instrument_id=@inst AND instrument_code=@code AND status='active'
                """, new { inst = g.InstrumentId, code = r.InstrumentTestCode }, tx);

            string? reason = null;
            if (mapping is null) reason = "unmapped code";
            else if (!mapping.Reportable) reason = "code not reportable";
            else if (mapping.InstrumentUnit is { } u && r.Units is { } ru && !string.Equals(u, ru, StringComparison.OrdinalIgnoreCase))
                reason = $"unit mismatch: expected {u}, received {ru}";
            else if (string.IsNullOrWhiteSpace(r.RawValue.Split('^')[0]))
                reason = "empty value";

            Guid? specimenTestId = null;
            if (reason is null)
            {
                specimenTestId = await conn.ExecuteScalarAsync<Guid?>("""
                    SELECT st.id FROM clinical.specimen_test st JOIN clinical.order_test ot ON ot.id=st.order_test_id
                    WHERE st.specimen_id=@sid AND ot.catalog_code=@code AND ot.status NOT IN ('cancelled','not_performed')
                    LIMIT 1
                    """, new { sid = specimen.Id, code = mapping!.CatalogCode }, tx);
                if (specimenTestId is null) reason = "test not ordered on this specimen";
            }

            ValueOutcome? value = null;
            CatalogRow? catalog = null;
            if (reason is null)
            {
                catalog = await conn.QuerySingleOrDefaultAsync<CatalogRow>("""
                    SELECT code, ucum_unit AS UcumUnit, decimals, ref_low AS RefLow, ref_high AS RefHigh,
                           critical_low AS CriticalLow, critical_high AS CriticalHigh
                    FROM clinical.test_catalog WHERE code=@code AND status='active'
                    """, new { code = mapping!.CatalogCode }, tx);
                if (catalog is null) reason = "catalog test not active";
                else
                {
                    value = ToValue(r, mapping, catalog);
                    if (value is null) reason = "unparsable value";
                }
            }

            if (reason == "code not reportable")
            {
                // Mapped on purpose as not reportable (for example BC-5000 research parameters the lab does not report):
                // kept for traceability, closed at once so it never sits in the exceptions queue.
                var kept = await InsertInstrumentResultAsync(conn, tx, item, g, r, fingerprint, "quarantined", reason, null, mapping?.Id);
                await conn.ExecuteAsync("UPDATE clinical.instrument_result SET resolved_at=now(), resolved_by='system:not-reportable' WHERE id=@kept",
                    new { kept }, tx);
                continue;
            }
            if (reason is not null)
            {
                await InsertInstrumentResultAsync(conn, tx, item, g, r, fingerprint, "quarantined", reason, specimenTestId, mapping?.Id);
                await audit.WriteAsync(conn, tx, new AuditEntry("system:inbox", "result.quarantined", "specimen", specimen.Id.ToString(),
                    Accession: specimen.Accession, PatientId: specimen.Mrn, CorrelationId: item.CorrelationId,
                    Reason: $"{r.InstrumentTestCode}: {reason}"));
                continue;
            }

            // Existing result for this specimen test?
            var latest = await conn.QuerySingleOrDefaultAsync<(Guid Id, Guid Group, int Version, string Status)?>("""
                SELECT id, result_group, version, status FROM clinical.result
                WHERE specimen_test_id=@st ORDER BY version DESC LIMIT 1 FOR UPDATE
                """, new { st = specimenTestId }, tx);
            if (latest is { Status: "approved" })
            {
                await InsertInstrumentResultAsync(conn, tx, item, g, r, fingerprint, "quarantined", "run received after release", specimenTestId, mapping!.Id);
                await audit.WriteAsync(conn, tx, new AuditEntry("system:inbox", "result.run_after_release", "result", latest.Value.Id.ToString(),
                    Accession: specimen.Accession, PatientId: specimen.Mrn, CorrelationId: item.CorrelationId));
                continue;
            }

            var irId = await InsertInstrumentResultAsync(conn, tx, item, g, r, fingerprint, "matched", null, specimenTestId, mapping!.Id);
            var resultId = Guid.CreateVersion7();
            var group = latest?.Group ?? Guid.CreateVersion7();
            var version = (latest?.Version ?? 0) + 1;
            if (latest is { } prev)
                await conn.ExecuteAsync("UPDATE clinical.result SET status='superseded' WHERE id=@id", new { id = prev.Id }, tx);

            await conn.ExecuteAsync("""
                INSERT INTO clinical.result (id, result_group, version, specimen_test_id, instrument_result_id, value_type, value_text,
                    numeric_value, comparator, units, reference_range, abnormal_flag, instrument_flags, flag_mismatch, is_critical,
                    status, supersedes_id, change_reason, mapping_id)
                VALUES (@resultId, @group, @version, @st, @irId, @ValueType, @ValueText, @Numeric, @Comparator, @Units, @RefRange,
                    @Abnormal, @flags, @FlagMismatch, @IsCritical, 'received', @supersedes, @changeReason, @mappingId)
                """, new
            {
                resultId, group, version, st = specimenTestId, irId, value!.ValueType, value.ValueText, value.Numeric, value.Comparator,
                value.Units, value.RefRange, value.Abnormal, flags = r.InstrumentFlags.ToArray(), value.FlagMismatch, value.IsCritical,
                supersedes = latest?.Id, changeReason = latest is null ? null : (r.IsRerun ? "rerun from instrument" : "new instrument run"),
                mappingId = mapping.Id,
            }, tx);
            await conn.ExecuteAsync("""
                UPDATE clinical.order_test SET status='resulted' WHERE id=(SELECT order_test_id FROM clinical.specimen_test WHERE id=@st)
                  AND status IN ('pending_at_instrument','pending_specimen','ordered','resulted')
                """, new { st = specimenTestId }, tx);
            var delta = await DeltaCheck.ApplyAsync(conn, tx, resultId);
            if (delta.Flagged)
                await audit.WriteAsync(conn, tx, new AuditEntry("system:inbox", "result.delta_flagged", "result", resultId.ToString(),
                    Accession: specimen.Accession, PatientId: specimen.Mrn, CorrelationId: item.CorrelationId,
                    After: new { value = value.ValueText, previous = delta.PreviousValue, previous_result = delta.PreviousId }));
            await audit.WriteAsync(conn, tx, new AuditEntry("system:inbox", "result.received", "result", resultId.ToString(),
                Accession: specimen.Accession, PatientId: specimen.Mrn, CorrelationId: item.CorrelationId,
                After: new { code = catalog!.Code, value = value.ValueText, value.Units, version, mapping = mapping.Id, rerun = r.IsRerun }));
        }
    }

    private async Task<Guid> InsertInstrumentResultAsync(NpgsqlConnection conn, NpgsqlTransaction tx, InboxItem item, NormalizedInstrumentMessage g,
        NormalizedResult r, string fingerprint, string disposition, string? reason, Guid? specimenTestId, Guid? mappingId)
    {
        var id = Guid.CreateVersion7();
        await conn.ExecuteAsync("""
            INSERT INTO clinical.instrument_result (id, inbox_message_id, instrument_id, sample_id, instrument_test_code, raw_value, units,
                comparator, numeric_value, qualitative_value, secondary_value, instrument_flags, reference_range_text, run_at, dilution,
                is_rerun, fingerprint, disposition, quarantine_reason, specimen_test_id, mapping_id, correlation_id)
            VALUES (@id, @inbox, @inst, @sid, @code, @raw, @units, @cmp, @num, @qual, @sec, @flags, @ref, @run, @dil,
                @rerun, @fingerprint, @disposition, @reason, @specimenTestId, @mappingId, @corr)
            """, new
        {
            id, inbox = item.Id, inst = g.InstrumentId, sid = g.SampleId, code = r.InstrumentTestCode, raw = r.RawValue, units = r.Units,
            cmp = r.Comparator, num = r.NumericValue, qual = r.QualitativeValue, sec = r.SecondaryValue, flags = r.InstrumentFlags.ToArray(),
            @ref = r.ReferenceRangeText, run = r.RunAt?.UtcDateTime, dil = r.Dilution, rerun = r.IsRerun, fingerprint, disposition, reason,
            specimenTestId, mappingId, corr = item.CorrelationId,
        }, tx);
        return id;
    }

    private sealed record ValueOutcome(string ValueType, string ValueText, decimal? Numeric, string? Comparator, string? Units,
        string? RefRange, string? Abnormal, bool FlagMismatch, bool IsCritical);

    private static readonly Dictionary<string, string> QualitativeText = new()
    {
        ["1"] = "Reactive", ["0"] = "Borderline", ["-1"] = "Non-reactive",
    };

    /// <summary>Applies mapping and catalog rules (design 5.7). Returns null when the value cannot be used.</summary>
    private static ValueOutcome? ToValue(NormalizedResult r, MappingRow m, CatalogRow c)
    {
        var refRange = c.RefLow is not null || c.RefHigh is not null
            ? $"{c.RefLow?.ToString(CultureInfo.InvariantCulture)}-{c.RefHigh?.ToString(CultureInfo.InvariantCulture)}"
            : r.ReferenceRangeText;
        var instrumentHl = r.InstrumentFlags.FirstOrDefault(f => f is "H" or "L" or "N" or "HH" or "LL");

        if (m.ResultType == "qualitative")
        {
            var primary = r.QualitativeValue ?? r.RawValue.Split('^')[0].Trim();
            if (!QualitativeText.TryGetValue(primary, out var text)) return null;
            decimal? coi = decimal.TryParse(r.SecondaryValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var ci) ? ci : null;
            return new ValueOutcome("qualitative", text, coi, null, r.Units, null, primary == "1" ? "A" : null, false, false);
        }
        if (m.ResultType == "text")
            return new ValueOutcome("text", r.RawValue.Trim(), null, null, r.Units, refRange, null, false, false);

        if (r.NumericValue is not { } raw) return null;
        var value = raw * m.UnitFactor;
        var converted = m.UnitFactor != 1m;
        var primaryText = r.RawValue.Split('^')[0].Trim();
        var valueText = converted
            ? (r.Comparator ?? "") + Math.Round(value, c.Decimals, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
            : primaryText; // preserved exactly as the instrument sent it (AC7)
        var units = converted ? c.UcumUnit : r.Units ?? c.UcumUnit;

        string? flag = null;
        var critical = false;
        if (c.CriticalLow is { } cl && value < cl && r.Comparator != ">") { flag = "LL"; critical = true; }
        else if (c.CriticalHigh is { } ch && value > ch && r.Comparator != "<") { flag = "HH"; critical = true; }
        else if (c.RefLow is { } lo && value < lo && r.Comparator != ">") flag = "L";
        else if (c.RefHigh is { } hi && value > hi && r.Comparator != "<") flag = "H";
        if (r.Comparator == ">" && c.CriticalHigh is { } ch2 && value >= ch2) { flag = "HH"; critical = true; }

        var computedSimple = flag switch { "LL" => "L", "HH" => "H", null => "N", _ => flag };
        var instrumentSimple = instrumentHl switch { "LL" => "L", "HH" => "H", _ => instrumentHl };
        var mismatch = instrumentSimple is not null && c.RefLow is not null && instrumentSimple != computedSimple;

        return new ValueOutcome("numeric", valueText, value, r.Comparator, units, refRange, flag, mismatch, critical);
    }
}
