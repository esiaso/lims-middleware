using System.Globalization;
using System.Text;
using Lims.Contracts;

namespace Lims.Instruments.Astm;

/// <summary>
/// Parser for the Roche cobas c 111 host interface, built from Host Interface Manual v2.2 (June 2021,
/// software 3.0 and higher), sections 7.2.2 (records) and 9 (communication examples).
/// DOCUMENTED, NOT interface-tested on an installed analyzer.
/// </summary>
/// <remarks>
/// Differences from the e411 that matter here:
/// <list type="bullet">
/// <item>The analyzer sends the sample ID in O-4 (instrument specimen ID, "SampleID^^Position"), not O-3.</item>
/// <item>QC orders carry action code Q (O-12) and the sample ID "1000 + control code" (for example "1300").</item>
/// <item>Result status (R-9): F initial, C rerun, R re-transmission of an already sent result, X not completed, I QC ignored.</item>
/// <item>Reference ranges arrive as "low\high" (repeat delimiter) in the examples, or "low/high" per the field text; both are read.</item>
/// <item>No qualitative results. M records (absorbance raw data, calibration) are not used.</item>
/// <item>H-11 message types: TSREQ (order query), RSUPL (results), ORUPL/RSREQ replies, PCUPL/ICUPL (calibration).</item>
/// </list>
/// </remarks>
public sealed class CobasC111Parser(TimeSpan instrumentUtcOffset) : IMessageParser
{
    public const string Id = "roche.cobas-c111.astm";
    public string ParserId => Id;

    public ParseOutcome Parse(string instrumentId, byte[] raw, string correlationId)
    {
        var text = Encoding.ASCII.GetString(raw);
        var records = AstmRecord.SplitMessage(text);
        if (records.Count == 0 || records[0].Type != 'H') return ParseOutcome.Fail("message does not start with an H record");
        var messageType = records[0].Component(11, 0).Trim().ToUpperInvariant();

        if (messageType is "PCUPL" or "ICUPL")
            return ParseOutcome.Ok([new NormalizedInstrumentMessage(instrumentId, MessageKind.Calibration, null, [], correlationId)]);

        var messages = new List<NormalizedInstrumentMessage>();
        string? sampleId = null, controlName = null;
        string? orderTimestamp = null;
        var isQc = false;
        var inOrder = false;
        var results = new List<NormalizedResult>();

        void FlushOrder()
        {
            if (!inOrder) return;
            messages.Add(new NormalizedInstrumentMessage(instrumentId, isQc ? MessageKind.Qc : MessageKind.PatientResult,
                sampleId, results.ToList(), correlationId, controlName, null));
            results.Clear();
            inOrder = false;
        }

        foreach (var r in records.Skip(1))
        {
            switch (r.Type)
            {
                case 'Q':
                    FlushOrder();
                    messages.Add(ParseQuery(instrumentId, r, correlationId));
                    break;
                case 'P':
                    FlushOrder();
                    break;
                case 'O':
                    FlushOrder();
                    // A reply to a host order query (ORUPL^REPLY) lists the orders the analyzer holds: no results.
                    inOrder = messageType != "ORUPL";
                    var instrumentSid = Nullify(r.Component(4, 0));
                    sampleId = instrumentSid ?? Nullify(r.Component(3, 0));
                    isQc = r.Repeats(12).Any(a => a.Trim() == "Q");
                    controlName = isQc ? sampleId : null;
                    orderTimestamp = Nullify(r.Text(23));
                    break;
                case 'R':
                    if (!inOrder) return ParseOutcome.Fail("R record without a preceding O record");
                    var parsed = ParseResult(r, orderTimestamp);
                    if (isQc && parsed.ResultStatus == "I") break; // QC result ignored on the instrument: not filed
                    results.Add(parsed);
                    break;
                case 'C':
                    // C|1|I|40^>RR\41^...|I : instrument flags for the preceding result
                    if (results.Count == 0 || r.Text(3).Trim() != "I") break;
                    var flags = r.Repeats(4).Select(rep => rep.Split(r.Delimiters.Component)[0].Trim()).Where(f => f.Length > 0)
                        .Select(f => $"alarm:{f}").ToList();
                    if (flags.Count > 0)
                        results[^1] = results[^1] with { InstrumentFlags = results[^1].InstrumentFlags.Concat(flags).ToList() };
                    break;
                case 'M':
                    break; // absorbance raw data / calibration detail
                case 'L':
                    FlushOrder();
                    break;
                default:
                    return ParseOutcome.Fail($"unknown record type '{r.Type}'");
            }
        }
        FlushOrder();
        return messages.Count == 0 ? ParseOutcome.Fail("message carries no query or order records") : ParseOutcome.Ok(messages);
    }

    private static NormalizedInstrumentMessage ParseQuery(string instrumentId, AstmRecord q, string correlationId)
    {
        // Q|1|^<SampleID>||ALL||||||||O   ; abort: Q|1|dummy||||||||||A
        var sid = Nullify(q.Component(3, 1)) ?? Nullify(q.Component(3, 0));
        var status = q.Text(13).Trim().ToUpperInvariant();
        return new NormalizedInstrumentMessage(instrumentId, MessageKind.Query, sid, [], correlationId,
            Query: new QueryDetails(null, null, null, null, null, IsCancel: status == "A"));
    }

    private NormalizedResult ParseResult(AstmRecord r, string? orderTimestamp)
    {
        var code = r.Component(3, 3).Trim();
        var treatment = Nullify(r.Component(3, 4).Trim());
        var status = Nullify(r.Text(9).Trim()?.ToUpperInvariant());
        var value = status == "X" ? "" : r.Text(4).Trim();

        string? comparator = null;
        var numericText = value;
        if (value.StartsWith('<') || value.StartsWith('>'))
        {
            comparator = value[..1];
            numericText = value[1..].Trim();
        }
        decimal? numeric = decimal.TryParse(numericText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

        var flags = new List<string>();
        var abnormal = r.Text(7).Trim();
        if (abnormal.Length > 0) flags.Add(abnormal);
        if (status == "X") flags.Add("not completed");
        if (status == "R") flags.Add("retransmission");

        var range = r.Field(6);
        string? refRange = null;
        if (range.Length > 0)
        {
            var parts = range.Split(r.Delimiters.Repeat, '/').Select(p => p.Trim()).Where(p => p.Length > 0).Take(2).ToArray();
            refRange = parts.Length == 2 ? $"{parts[0]}-{parts[1]}" : parts.FirstOrDefault();
        }

        return new NormalizedResult(
            InstrumentTestCode: code,
            RawValue: value,
            Units: Nullify(r.Text(5).Trim()),
            Comparator: comparator,
            NumericValue: numeric,
            QualitativeValue: null,
            SecondaryValue: null,
            InstrumentFlags: flags,
            ReferenceRangeText: refRange,
            RunAt: ParseTimestamp(Nullify(r.Text(13)) ?? orderTimestamp),
            Dilution: treatment,
            IsRerun: status == "C",
            OperatorId: Nullify(r.Text(11).Trim()),
            // A re-transmission repeats an already sent result: same status as the original so duplicates are detected.
            ResultStatus: status == "R" ? "F" : status);
    }

    private DateTimeOffset? ParseTimestamp(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParseExact(s.Trim(), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? new DateTimeOffset(dt, instrumentUtcOffset)
            : null;
    }

    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>Host reply to a c111 real-time order query (manual 9.2), TSDWN^REPLY.</summary>
public static class CobasC111QueryReply
{
    public const int MaxTestsPerOrder = 60;

    public static string Build(NormalizedInstrumentMessage query, InstrumentOrder? order, DateTimeOffset now)
    {
        var sid = query.SampleId ?? "";
        var o = new string[26];
        Array.Fill(o, "");
        o[0] = "O";
        o[1] = "1";
        o[2] = sid;
        o[5] = order is null ? "R" : order.Priority == "stat" ? "S" : "R";
        o[11] = "A";
        if (order is null)
        {
            o[25] = "Z"; // no order for this sample (manual 7.2.2.5)
        }
        else
        {
            o[4] = string.Join("\\", order.InstrumentTestCodes.Distinct().Take(MaxTestsPerOrder).Select(c => $"^^^{c}^"));
            o[25] = "O\\Q";
        }
        return string.Join("\r",
            $"H|\\^&|||host^1|||||c111|TSDWN^REPLY|P|1|{now:yyyyMMddHHmmss}",
            "P|1",
            string.Join("|", o),
            "L|1|N") + "\r";
    }
}
