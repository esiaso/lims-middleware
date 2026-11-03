using System.Globalization;
using System.Text;
using Lims.Contracts;

namespace Lims.Instruments.Hl7;

/// <summary>
/// Parser for the Mindray BC-5000 "3107 HL7 Communication Protocol" V2.0 (HL7 v2.3.1 over MLLP, UTF-8).
/// DOCUMENTED, NOT interface-tested on an installed analyzer.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>ORU^R01: one OBR per sample, OBR-3 = sample ID, OBR-4 "00001" patient count or "00003" QC count; MSH-11 "Q" also marks QC.</item>
/// <item>OBX-3 is "ID^Name^EncodeSys"; the instrument test code used for mapping is the ID (LOINC such as 6690-2, or a 99MRC code).</item>
/// <item>Non-result OBX items (modes, remarks, age, reference group, histogram and scattergram data) are not results.</item>
/// <item>IS items with value "T" are suspect-cell and morphology alerts; they are attached as flags to every result of the sample.</item>
/// <item>ORM^O01 with ORC-1 "RF" is a worklist query; ORC-3 carries the sample ID.</item>
/// </list>
/// </remarks>
public sealed class MindrayBc5000Parser(TimeSpan instrumentUtcOffset) : IMessageParser
{
    public const string Id = "mindray.bc5000.hl7";
    public string ParserId => Id;

    /// <summary>OBX-3 IDs that describe the run, not a patient result (protocol 5.8, "Non-parameter Data Items").</summary>
    public static readonly HashSet<string> NonResultCodes = new(StringComparer.Ordinal)
    {
        "08001", "08002", "08003", "01001", "01002", "01006", "05001", "30525-0",
        "10003", "10004", "10005", "10006", // QC-specific scatter positions
    };

    public static bool IsNonResult(string code, string encodeSys, string valueType) =>
        NonResultCodes.Contains(code)
        || valueType is "ED" or "IS"
        || (encodeSys == "99MRC" && code.StartsWith("15", StringComparison.Ordinal)); // histogram/scattergram data

    public ParseOutcome Parse(string instrumentId, byte[] raw, string correlationId)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(raw); }
        catch (DecoderFallbackException) { return ParseOutcome.Fail("message is not valid UTF-8"); }
        var msg = Hl7Message.TryParse(text, out var error);
        if (msg is null) return ParseOutcome.Fail(error!);

        switch (msg.MessageType, msg.TriggerEvent)
        {
            case ("ORM", "O01"):
                return ParseQuery(instrumentId, msg, correlationId);
            case ("ORU", "R01"):
                return ParseResults(instrumentId, msg, correlationId);
            default:
                return ParseOutcome.Fail($"unsupported message type {msg.MessageType}^{msg.TriggerEvent}");
        }
    }

    private static ParseOutcome ParseQuery(string instrumentId, Hl7Message msg, string correlationId)
    {
        var orc = msg.First("ORC");
        if (orc is null) return ParseOutcome.Fail("ORM^O01 without ORC segment");
        var sid = Nullify(orc.Text(3)) ?? Nullify(orc.Text(2));
        return ParseOutcome.Ok([new NormalizedInstrumentMessage(instrumentId, MessageKind.Query, sid, [], correlationId,
            Query: new QueryDetails(msg.ControlId, null, null, null, null, IsCancel: false))]);
    }

    private ParseOutcome ParseResults(string instrumentId, Hl7Message msg, string correlationId)
    {
        var messages = new List<NormalizedInstrumentMessage>();
        var qcMessage = msg.ProcessingId.Trim().Equals("Q", StringComparison.OrdinalIgnoreCase);
        string? controlLot = null;

        Hl7Segment? obr = null;
        var results = new List<NormalizedResult>();
        var alerts = new List<string>();

        void Flush()
        {
            if (obr is null) return;
            var isQc = qcMessage || obr.Component(4, 0).Trim() == "00003";
            var sampleId = Nullify(obr.Text(3).Trim());
            var withAlerts = alerts.Count == 0
                ? results.ToList()
                : results.Select(r => r with { InstrumentFlags = r.InstrumentFlags.Concat(alerts).ToList() }).ToList();
            messages.Add(new NormalizedInstrumentMessage(instrumentId, isQc ? MessageKind.Qc : MessageKind.PatientResult,
                sampleId, withAlerts, correlationId, isQc ? sampleId : null, isQc ? controlLot : null));
            results.Clear();
            alerts.Clear();
            obr = null;
        }

        foreach (var seg in msg.Segments.Skip(1))
        {
            switch (seg.Name)
            {
                case "PID":
                    Flush();
                    controlLot = Nullify(seg.Component(3, 0)); // QC messages: control lot number (protocol 4.3.3)
                    break;
                case "PV1":
                    break;
                case "OBR":
                    Flush();
                    obr = seg;
                    break;
                case "OBX":
                    if (obr is null) return ParseOutcome.Fail("OBX segment before any OBR segment");
                    var valueType = seg.Text(2).Trim();
                    var code = seg.Component(3, 0).Trim();
                    var name = seg.Component(3, 1).Trim();
                    var encodeSys = seg.Component(3, 2).Trim();
                    if (code.Length == 0 && name.Length == 0) break;
                    if (IsNonResult(code, encodeSys, valueType))
                    {
                        if (valueType == "IS" && !NonResultCodes.Contains(code) && seg.Text(5).Trim().Equals("T", StringComparison.OrdinalIgnoreCase))
                            alerts.Add($"alert:{(name.Length > 0 ? name : code)}");
                        break;
                    }
                    results.Add(ParseObx(seg, code, obr));
                    break;
                default:
                    break; // NTE and others are not used by the BC-5000
            }
        }
        Flush();
        return messages.Count == 0 ? ParseOutcome.Fail("ORU^R01 without OBR segments") : ParseOutcome.Ok(messages);
    }

    private NormalizedResult ParseObx(Hl7Segment obx, string code, Hl7Segment obr)
    {
        var value = obx.Text(5).Trim();
        string? comparator = null;
        var numericText = value;
        if (value.StartsWith('<') || value.StartsWith('>'))
        {
            comparator = value[..1];
            numericText = value[1..].Trim();
        }
        decimal? numeric = decimal.TryParse(numericText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

        var flags = new List<string>();
        foreach (var f in obx.Field(8).Split(obx.Delimiters.Repeat).Select(f => f.Trim()).Where(f => f.Length > 0))
            if (f != "N") flags.Add(f);
        foreach (var f in obx.Field(13).Split(obx.Delimiters.Repeat).Select(f => f.Trim()).Where(f => f.Length > 0))
            flags.Add(f switch { "O" => "reagent expired", "E" => "edited on instrument", "e" => "recalculated after edit", _ => $"check:{f}" });
        if (obx.Component(3, 1).StartsWith('*')) flags.Add("RUO"); // research-use-only parameter (ALY, LIC)

        var status = Nullify(obx.Text(11).Trim());
        return new NormalizedResult(
            InstrumentTestCode: code,
            RawValue: value,
            Units: Nullify(obx.Component(6, 0).Trim()),
            Comparator: comparator,
            NumericValue: numeric,
            QualitativeValue: null,
            SecondaryValue: null,
            InstrumentFlags: flags,
            ReferenceRangeText: Nullify(obx.Text(7).Trim()),
            RunAt: ParseTimestamp(Nullify(obx.Text(14)) ?? Nullify(obr.Text(7))),
            Dilution: null,
            IsRerun: string.Equals(status, "C", StringComparison.Ordinal),
            OperatorId: Nullify(obr.Component(32, 0).Trim()),
            ResultStatus: status);
    }

    private DateTimeOffset? ParseTimestamp(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        string[] formats = ["yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd"];
        return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? new DateTimeOffset(dt, instrumentUtcOffset)
            : null;
    }

    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>Messages the host sends to the BC-5000: ACK^R01 for results and ORR^O02 for worklist queries.</summary>
public static class MindrayBc5000Replies
{
    /// <summary>Differential parameters (protocol 5.8). An order containing any of them runs in CBC+DIFF mode.</summary>
    public static readonly HashSet<string> DiffCodes = new(StringComparer.Ordinal)
    {
        "704-7", "706-2", "751-8", "770-8", "711-2", "713-8", "731-0", "736-9", "742-7", "5905-5",
    };

    public static string Ack(string ackCode, string receivedControlId, string processingId, DateTimeOffset now, int? errorCode = null, string? errorText = null)
    {
        var msa = errorCode is null ? $"MSA|{ackCode}|{Esc(receivedControlId)}" : $"MSA|{ackCode}|{Esc(receivedControlId)}|{Esc(errorText ?? "")}||||{errorCode}";
        return $"MSH|^~\\&|LIS||||{now:yyyyMMddHHmmss}||ACK^R01|{now:HHmmssfff}|{Pid(processingId)}|2.3.1||||||UNICODE\r{msa}\r";
    }

    /// <summary>
    /// Worklist reply (protocol 5.5): ORC-1 "AF", ORC-2 and OBR-2 = sample ID, OBX 08003 Test Mode.
    /// With no order the reply carries MSH and MSA only. The exact "no order" form and the accepted Test Mode values
    /// must be confirmed with Mindray (design 4.1).
    /// </summary>
    public static string WorklistReply(string receivedControlId, string? sampleId, InstrumentOrder? order, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append($"MSH|^~\\&|LIS||||{now:yyyyMMddHHmmss}||ORR^O02|{now:HHmmssfff}|P|2.3.1||||||UNICODE\r");
        sb.Append($"MSA|AA|{Esc(receivedControlId)}\r");
        if (order is null || sampleId is null) return sb.ToString();
        var sid = Esc(sampleId);
        var mode = order.InstrumentTestCodes.Any(DiffCodes.Contains) ? "CBC+DIFF" : "CBC";
        sb.Append("PID|1\r");
        sb.Append("PV1|1\r");
        sb.Append($"ORC|AF|{sid}\r");
        sb.Append($"OBR|1|{sid}||00001^Automated Count^99MRC\r");
        sb.Append("OBX|1|IS|08002^Blood Mode^99MRC||W||||||F\r");
        sb.Append($"OBX|2|IS|08003^Test Mode^99MRC||{mode}||||||F\r");
        return sb.ToString();
    }

    private static string Pid(string processingId) => processingId.Trim().Equals("Q", StringComparison.OrdinalIgnoreCase) ? "Q" : "P";
    private static string Esc(string s) => Hl7Segment.Escape(s, Hl7Delimiters.Default);
}
