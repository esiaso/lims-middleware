using System.Globalization;
using System.Text;
using Lims.Contracts;
using Lims.Instruments.Hl7;

namespace Lims.Instruments.Generic;

/// <summary>
/// Configurable HL7 v2 parser for analyzers registered from the screens. Results are read from OBX segments grouped
/// under OBR; field positions come from the analyzer's <see cref="GenericAdapterProfile"/>. CONFIGURED, NOT
/// interface-tested: an analyzer registered this way is "unverified" until its site verification is signed.
/// </summary>
public sealed class GenericHl7Parser(GenericAdapterProfile profile, TimeSpan instrumentUtcOffset) : IMessageParser
{
    public const string Id = "generic.hl7";
    public string ParserId => Id;

    private readonly FieldRef _sampleId = FieldRef.Parse(profile.Hl7SampleId);
    private readonly FieldRef? _sampleIdFallback = FieldRef.ParseOptional(profile.Hl7SampleIdFallback);
    private readonly FieldRef _testCode = FieldRef.Parse(profile.Hl7TestCode);
    private readonly FieldRef _value = FieldRef.Parse(profile.Hl7Value);
    private readonly FieldRef? _units = FieldRef.ParseOptional(profile.Hl7Units);
    private readonly FieldRef? _range = FieldRef.ParseOptional(profile.Hl7ReferenceRange);
    private readonly FieldRef? _flag = FieldRef.ParseOptional(profile.Hl7AbnormalFlag);
    private readonly FieldRef? _status = FieldRef.ParseOptional(profile.Hl7ResultStatus);
    private readonly FieldRef? _operator = FieldRef.ParseOptional(profile.Hl7Operator);
    private readonly FieldRef? _completedAt = FieldRef.ParseOptional(profile.Hl7CompletedAt);
    private readonly FieldRef? _querySampleId = FieldRef.ParseOptional(profile.Hl7QuerySampleId);
    private readonly HashSet<string> _ignored = new(profile.IgnoredTestCodes.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);

    public ParseOutcome Parse(string instrumentId, byte[] raw, string correlationId)
    {
        string text;
        try { text = Decode(profile, raw); }
        catch (DecoderFallbackException) { return ParseOutcome.Fail("message is not valid UTF-8"); }
        var msg = Hl7Message.TryParse(text, out var error);
        if (msg is null) return ParseOutcome.Fail(error!);
        var type = $"{msg.MessageType}^{msg.TriggerEvent}";

        if (type.Equals(profile.Hl7QueryMessage, StringComparison.OrdinalIgnoreCase))
            return ParseQuery(instrumentId, msg, correlationId);
        if (msg.MessageType == "ACK")
            return ParseOutcome.Ok([new NormalizedInstrumentMessage(instrumentId, MessageKind.Status, null, [], correlationId)]);
        return ParseResults(instrumentId, msg, correlationId);
    }

    public static string Decode(GenericAdapterProfile profile, byte[] raw) =>
        profile.TextEncoding() is UTF8Encoding ? new UTF8Encoding(false, true).GetString(raw) : profile.TextEncoding().GetString(raw);

    private ParseOutcome ParseQuery(string instrumentId, Hl7Message msg, string correlationId)
    {
        string? sid = null;
        if (_querySampleId is not null && msg.First(_querySampleId.Record) is { } seg) sid = Nullify(Read(seg, _querySampleId));
        return ParseOutcome.Ok([new NormalizedInstrumentMessage(instrumentId, MessageKind.Query, sid, [], correlationId,
            Query: new QueryDetails(msg.ControlId, null, null, null, null, IsCancel: false))]);
    }

    private ParseOutcome ParseResults(string instrumentId, Hl7Message msg, string correlationId)
    {
        var qcMessage = profile.Hl7QcWhenProcessingIdQ && msg.ProcessingId.Trim().Equals("Q", StringComparison.OrdinalIgnoreCase);
        var ctx = new Dictionary<string, Hl7Segment>(StringComparer.Ordinal) { ["MSH"] = msg.Msh };
        var messages = new List<NormalizedInstrumentMessage>();
        var results = new List<NormalizedResult>();
        var inGroup = false;

        void Flush()
        {
            if (!inGroup) return;
            var sampleId = Nullify(Read(ctx, _sampleId)) ?? (_sampleIdFallback is null ? null : Nullify(Read(ctx, _sampleIdFallback)));
            var isQc = qcMessage || (sampleId is not null && profile.QcSampleIdPrefixes.Any(p => p.Length > 0 && sampleId.StartsWith(p.Trim(), StringComparison.OrdinalIgnoreCase)));
            messages.Add(new NormalizedInstrumentMessage(instrumentId, isQc ? MessageKind.Qc : MessageKind.PatientResult,
                sampleId, results.ToList(), correlationId, isQc ? sampleId : null, null));
            results.Clear();
            inGroup = false;
        }

        foreach (var seg in msg.Segments.Skip(1))
        {
            switch (seg.Name)
            {
                case "PID":
                    Flush();
                    ctx.Remove("OBR");
                    ctx.Remove("ORC");
                    ctx.Remove("SPM");
                    ctx["PID"] = seg;
                    break;
                case "ORC" or "SPM":
                    Flush(); // starts the next order group: keep it from relabelling the one before
                    ctx[seg.Name] = seg;
                    break;
                case "OBR":
                    Flush();
                    ctx["OBR"] = seg;
                    inGroup = true;
                    break;
                case "OBX":
                    if (!inGroup) return ParseOutcome.Fail("OBX segment before any OBR segment");
                    ctx["OBX"] = seg;
                    var r = ParseObx(ctx);
                    if (r is not null) results.Add(r);
                    break;
                case "NTE":
                    if (results.Count > 0 && Nullify(seg.Text(3)) is { } note)
                        results[^1] = results[^1] with { InstrumentFlags = results[^1].InstrumentFlags.Append($"note:{note}").ToList() };
                    break;
                default:
                    ctx[seg.Name] = seg; // ORC, PV1, SPM and others: available to field positions
                    break;
            }
        }
        Flush();
        return messages.Count == 0 ? ParseOutcome.Fail($"{msg.MessageType}^{msg.TriggerEvent} without OBR segments") : ParseOutcome.Ok(messages);
    }

    private NormalizedResult? ParseObx(Dictionary<string, Hl7Segment> ctx)
    {
        var obx = ctx["OBX"];
        var code = Read(ctx, _testCode).Trim();
        if (code.Length == 0 || _ignored.Contains(code)) return null;
        if (obx.Text(2).Trim() is "ED" or "RP") return null; // embedded data (images, histograms) is not a result
        var value = Read(ctx, _value).Trim();
        var (comparator, numeric) = GenericValues.Numeric(value);

        var flags = new List<string>();
        if (_flag is not null && ctx.TryGetValue(_flag.Record, out var flagSeg))
            foreach (var f in flagSeg.Field(_flag.Field).Split(flagSeg.Delimiters.Repeat).Select(f => flagSeg.Unescape(f).Trim()).Where(f => f.Length > 0))
                if (!profile.NormalFlagValues.Contains(f, StringComparer.OrdinalIgnoreCase)) flags.Add(f);

        var status = Nullify(Read(ctx, _status));
        return new NormalizedResult(
            InstrumentTestCode: code,
            RawValue: value,
            Units: Nullify(Read(ctx, _units)),
            Comparator: comparator,
            NumericValue: numeric,
            QualitativeValue: null,
            SecondaryValue: null,
            InstrumentFlags: flags,
            ReferenceRangeText: Nullify(Read(ctx, _range)),
            RunAt: GenericValues.Timestamp(Nullify(Read(ctx, _completedAt)) ?? Nullify(ctx.GetValueOrDefault("OBR")?.Text(7)),
                profile.TimestampFormats, instrumentUtcOffset),
            Dilution: null,
            IsRerun: status is not null && profile.RerunStatusValues.Contains(status, StringComparer.OrdinalIgnoreCase),
            OperatorId: Nullify(Read(ctx, _operator)),
            ResultStatus: status);
    }

    private static string Read(Dictionary<string, Hl7Segment> ctx, FieldRef? f) =>
        f is not null && ctx.TryGetValue(f.Record, out var seg) ? Read(seg, f) : "";

    private static string Read(Hl7Segment seg, FieldRef f) => f.Component is { } c ? seg.Component(f.Field, c - 1) : seg.Text(f.Field);

    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Messages the middleware sends to a generic HL7 analyzer: ACK, order query reply and pushed order.</summary>
public static class GenericHl7Messages
{
    public static string Msh(GenericAdapterProfile p, string messageType, string processingId, DateTimeOffset now) =>
        $"MSH|^~\\&|{Esc(p.HostName)}||{Esc(p.ReceiverName)}||{now:yyyyMMddHHmmss}||{messageType}|{now:yyyyMMddHHmmssfff}|{(processingId.Trim().Equals("Q", StringComparison.OrdinalIgnoreCase) ? "Q" : "P")}|{p.Hl7Version}\r";

    public static string Ack(GenericAdapterProfile p, string ackCode, string receivedControlId, string processingId, string? triggerEvent,
        DateTimeOffset now, int? errorCode = null, string? errorText = null)
    {
        var msa = errorCode is null ? $"MSA|{ackCode}|{Esc(receivedControlId)}" : $"MSA|{ackCode}|{Esc(receivedControlId)}|{Esc(errorText ?? "")}||||{errorCode}";
        var type = string.IsNullOrWhiteSpace(triggerEvent) ? "ACK" : $"ACK^{triggerEvent}";
        return Msh(p, type, processingId, now) + msa + "\r";
    }

    /// <summary>
    /// Answer to an order query: MSH, MSA AA, then PID, ORC (OK) and one OBR per test with OBR-2 the sample ID and OBR-4
    /// the instrument code. With no order the reply carries MSH and MSA only. Confirm the layout with the vendor.
    /// </summary>
    public static string QueryReply(GenericAdapterProfile p, string receivedControlId, string? sampleId, InstrumentOrder? order, DateTimeOffset now)
    {
        var sb = new StringBuilder(Msh(p, p.Hl7QueryReplyMessage, "P", now));
        sb.Append($"MSA|AA|{Esc(receivedControlId)}\r");
        if (order is null || sampleId is null || order.InstrumentTestCodes.Count == 0) return sb.ToString();
        AppendOrder(sb, p, sampleId, order, "OK");
        return sb.ToString();
    }

    /// <summary>A pushed new order (ORC-1 NW): PID, ORC and one OBR per test.</summary>
    public static string Order(GenericAdapterProfile p, InstrumentOrder order, DateTimeOffset now)
    {
        var sb = new StringBuilder(Msh(p, p.Hl7OrderMessage, "P", now));
        AppendOrder(sb, p, order.SampleId, order, "NW");
        return sb.ToString();
    }

    private static void AppendOrder(StringBuilder sb, GenericAdapterProfile p, string sampleId, InstrumentOrder order, string orderControl)
    {
        var sid = Esc(sampleId);
        var priority = order.Priority == "stat" ? "S" : "R";
        sb.Append("PID|1\r");
        sb.Append(CultureInfo.InvariantCulture, $"ORC|{orderControl}|{sid}\r");
        var n = 1;
        foreach (var code in order.InstrumentTestCodes.Distinct().Take(p.MaxTestsPerOrder))
            sb.Append(CultureInfo.InvariantCulture, $"OBR|{n++}|{sid}||{Esc(code)}|{priority}\r");
    }

    private static string Esc(string s) => Hl7Segment.Escape(s, Hl7Delimiters.Default);
}
