namespace Lims.Contracts;

/// <summary>What kind of content a parsed instrument message carries.</summary>
public enum MessageKind
{
    PatientResult,
    Qc,
    Calibration,
    Query,
    Status
}

/// <summary>
/// One sample's worth of normalized content parsed from a raw instrument message (design 6.1).
/// A single ASTM transmission can carry several of these (batch upload).
/// </summary>
public sealed record NormalizedInstrumentMessage(
    string InstrumentId,
    MessageKind Kind,
    string? SampleId,
    IReadOnlyList<NormalizedResult> Results,
    string CorrelationId,
    string? ControlName = null,
    string? ControlLot = null,
    QueryDetails? Query = null);

/// <summary>Instrument-side position data that a host-query reply must echo back unchanged.</summary>
public sealed record QueryDetails(
    string? SequenceNumber,
    string? CarrierNumber,
    string? PositionNumber,
    string? SampleType,
    string? ContainerType,
    bool IsCancel);

public sealed record NormalizedResult(
    string InstrumentTestCode,
    string RawValue,
    string? Units,
    string? Comparator,
    decimal? NumericValue,
    string? QualitativeValue,
    string? SecondaryValue,
    IReadOnlyList<string> InstrumentFlags,
    string? ReferenceRangeText,
    DateTimeOffset? RunAt,
    string? Dilution,
    bool IsRerun,
    string? OperatorId,
    string? ResultStatus);

public sealed record ParseOutcome(
    bool Success,
    IReadOnlyList<NormalizedInstrumentMessage> Messages,
    string? Error)
{
    public static ParseOutcome Ok(IReadOnlyList<NormalizedInstrumentMessage> messages) => new(true, messages, null);
    public static ParseOutcome Fail(string error) => new(false, Array.Empty<NormalizedInstrumentMessage>(), error);
}

/// <summary>Pure parser for one instrument protocol. Runs in the inbox processor, never in the link loop.</summary>
public interface IMessageParser
{
    string ParserId { get; }
    ParseOutcome Parse(string instrumentId, byte[] raw, string correlationId);
}

/// <summary>An ordered test as an instrument needs to see it in a host-query reply.</summary>
public sealed record InstrumentOrder(string SampleId, string Priority, IReadOnlyList<string> InstrumentTestCodes);

/// <summary>
/// Implemented by the clinical module: answers host queries from accepted specimens only
/// (never from the HMIS directly).
/// </summary>
public interface IInstrumentOrderLookup
{
    Task<InstrumentOrder?> GetOrdersForSampleAsync(string instrumentId, string sampleId, CancellationToken ct);
}

/// <summary>Resolves the parser configured for an instrument (implemented by the instruments module).</summary>
public interface IInstrumentParserResolver
{
    IMessageParser? ForInstrument(string instrumentId);
}
