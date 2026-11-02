using System.Text.Json.Serialization;

namespace Lims.Contracts;

// Shapes of the PROPOSED HMIS lab API (design section 6.9). These are not an agreed contract yet:
// when the HMIS team publishes the OpenAPI file, these records are regenerated from it.

public sealed record HmisPatient(
    [property: JsonPropertyName("mrn")] string Mrn,
    [property: JsonPropertyName("given_name")] string? GivenName,
    [property: JsonPropertyName("family_name")] string? FamilyName,
    [property: JsonPropertyName("sex")] string? Sex,
    [property: JsonPropertyName("birth_date")] DateOnly? BirthDate,
    [property: JsonPropertyName("national_id")] string? NationalId,
    [property: JsonPropertyName("sha_number")] string? ShaNumber);

public sealed record HmisEncounter(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("location")] string? Location);

public sealed record HmisOrderLine(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("test_code")] string TestCode,
    [property: JsonPropertyName("loinc")] string? Loinc,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("status")] string Status);

public sealed record HmisOrder(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("priority")] string Priority,
    [property: JsonPropertyName("patient")] HmisPatient Patient,
    [property: JsonPropertyName("encounter")] HmisEncounter Encounter,
    [property: JsonPropertyName("ordering_clinician")] string? OrderingClinician,
    [property: JsonPropertyName("lines")] IReadOnlyList<HmisOrderLine> Lines);

public sealed record HmisOrderPage(
    [property: JsonPropertyName("data")] IReadOnlyList<HmisOrder> Data,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

public sealed record HmisOrderEvent(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("order_id")] string OrderId,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt);

/// <summary>One approved result as sent to POST /results (or an amendment).</summary>
public sealed record HmisResultItem(
    [property: JsonPropertyName("middleware_result_id")] Guid MiddlewareResultId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("order_id")] string OrderId,
    [property: JsonPropertyName("order_line_id")] string OrderLineId,
    [property: JsonPropertyName("encounter_id")] string EncounterId,
    [property: JsonPropertyName("mrn")] string Mrn,
    [property: JsonPropertyName("accession")] string Accession,
    [property: JsonPropertyName("test_code")] string TestCode,
    [property: JsonPropertyName("loinc")] string? Loinc,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("value_type")] string ValueType,
    [property: JsonPropertyName("comparator")] string? Comparator,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("reference_range")] string? ReferenceRange,
    [property: JsonPropertyName("abnormal_flag")] string? AbnormalFlag,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("performed_at")] DateTimeOffset? PerformedAt,
    [property: JsonPropertyName("approved_at")] DateTimeOffset ApprovedAt,
    [property: JsonPropertyName("approved_by")] string ApprovedBy,
    [property: JsonPropertyName("instrument")] string? Instrument,
    [property: JsonPropertyName("comment")] string? Comment);

/// <summary>Payload stored in outbox_message for a result delivery.</summary>
public sealed record ResultDeliveryPayload(
    [property: JsonPropertyName("result")] HmisResultItem Result,
    [property: JsonPropertyName("supersedes_result_id")] Guid? SupersedesResultId,
    [property: JsonPropertyName("amendment_reason")] string? AmendmentReason);

public sealed record HmisResultsRequest([property: JsonPropertyName("results")] IReadOnlyList<HmisResultItem> Results);

public sealed record HmisResultAck(
    [property: JsonPropertyName("middleware_result_id")] Guid MiddlewareResultId,
    [property: JsonPropertyName("hmis_result_id")] string HmisResultId);

public sealed record HmisResultsResponse([property: JsonPropertyName("results")] IReadOnlyList<HmisResultAck> Results);

public sealed record HmisAmendmentRequest(
    [property: JsonPropertyName("result")] HmisResultItem Result,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record HmisCatalogEntry(
    [property: JsonPropertyName("test_code")] string TestCode,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("loinc")] string? Loinc,
    [property: JsonPropertyName("unit")] string? Unit);
