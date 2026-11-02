using System.Globalization;
using System.Text;
using Lims.Contracts;

namespace Lims.Instruments.Astm;

public enum E411ProtocolVariant { Cobas, Elecsys }

/// <summary>
/// Parser for the Roche cobas e 411 host interface, built from Host Interface Manual v2.2 (Nov 2013),
/// sections E (record structure) and F (communication traces). Handles both "cobas type" and
/// "Elecsys type" layouts. DOCUMENTED, NOT interface-tested on an installed analyzer.
/// </summary>
public sealed class CobasE411Parser(TimeSpan instrumentUtcOffset) : IMessageParser
{
    public const string Id = "roche.cobas-e411.astm";
    public string ParserId => Id;

    public ParseOutcome Parse(string instrumentId, byte[] raw, string correlationId)
    {
        string text;
        try { text = Encoding.ASCII.GetString(raw); }
        catch (Exception ex) { return ParseOutcome.Fail($"undecodable message: {ex.Message}"); }

        var records = AstmRecord.SplitMessage(text);
        if (records.Count == 0 || records[0].Type != 'H') return ParseOutcome.Fail("message does not start with an H record");
        var header = records[0];
        var variant = DetectVariant(header);

        var messages = new List<NormalizedInstrumentMessage>();
        string? sampleId = null, controlName = null, controlLot = null;
        var isQc = false;
        string? orderTimestamp = null;
        var results = new List<NormalizedResult>();
        var flagsForLast = new List<string>();
        var inOrder = false;

        void FlushOrder()
        {
            if (!inOrder) return;
            FlushComment();
            messages.Add(new NormalizedInstrumentMessage(instrumentId, isQc ? MessageKind.Qc : MessageKind.PatientResult,
                sampleId, results.ToList(), correlationId, controlName, controlLot));
            results.Clear();
            inOrder = false;
        }

        void FlushComment()
        {
            if (results.Count == 0 || flagsForLast.Count == 0) return;
            var last = results[^1];
            results[^1] = last with { InstrumentFlags = last.InstrumentFlags.Concat(flagsForLast).ToList() };
            flagsForLast.Clear();
        }

        foreach (var r in records.Skip(1))
        {
            switch (r.Type)
            {
                case 'Q':
                    FlushOrder();
                    messages.Add(ParseQuery(instrumentId, r, variant, correlationId));
                    break;
                case 'P':
                    FlushOrder();
                    break;
                case 'O':
                    FlushOrder();
                    inOrder = true;
                    (sampleId, isQc, controlName, controlLot) = ParseOrderIdentity(r, variant);
                    orderTimestamp = variant == E411ProtocolVariant.Cobas ? r.Text(23) : r.Text(7);
                    break;
                case 'R':
                    if (!inOrder) return ParseOutcome.Fail("R record without a preceding O record");
                    FlushComment();
                    results.Add(ParseResult(r, variant, orderTimestamp));
                    break;
                case 'C':
                    var code = r.Component(4, 0).Trim();
                    if (code.Length > 0 && results.Count > 0) flagsForLast.Add($"alarm:{code}");
                    break;
                case 'L':
                    FlushOrder();
                    break;
                case 'M':
                    break; // manufacturer records are not used by the e411 (manual A-2)
                default:
                    return ParseOutcome.Fail($"unknown record type '{r.Type}'");
            }
        }
        FlushOrder();
        return messages.Count == 0 ? ParseOutcome.Fail("message carries no query or order records") : ParseOutcome.Ok(messages);
    }

    public static E411ProtocolVariant DetectVariant(AstmRecord header)
    {
        // cobas type: sender "cobas-e411^1" (field 5) and message type such as RSUPL^REAL (field 11)
        var sender = header.Component(5, 0);
        var msgType = header.Component(11, 0);
        if (sender.StartsWith("cobas", StringComparison.OrdinalIgnoreCase) || msgType.Length > 0) return E411ProtocolVariant.Cobas;
        return E411ProtocolVariant.Elecsys;
    }

    private static NormalizedInstrumentMessage ParseQuery(string instrumentId, AstmRecord q, E411ProtocolVariant v, string correlationId)
    {
        // cobas:   ^^SampleID^SeqNo^Carrier^Pos^^SampleType^Container
        // Elecsys: ^SampleID^SeqNo^Carrier^Pos^^SampleType^Container
        var o = v == E411ProtocolVariant.Cobas ? 2 : 1;
        var sid = Nullify(q.Component(3, o));
        var details = new QueryDetails(
            Nullify(q.Component(3, o + 1)), Nullify(q.Component(3, o + 2)), Nullify(q.Component(3, o + 3)),
            Nullify(q.Component(3, o + 5)), Nullify(q.Component(3, o + 6)),
            IsCancel: q.Text(13).Trim().Equals("A", StringComparison.OrdinalIgnoreCase));
        return new NormalizedInstrumentMessage(instrumentId, MessageKind.Query, sid, Array.Empty<NormalizedResult>(), correlationId, Query: details);
    }

    private static (string? SampleId, bool IsQc, string? ControlName, string? ControlLot) ParseOrderIdentity(AstmRecord o, E411ProtocolVariant v)
    {
        var sid = Nullify(o.Component(3, 0));
        var action = o.Field(12);
        var specimenKind = v == E411ProtocolVariant.Cobas ? o.Component(4, 4) : o.Component(4, 4);
        var isQc = action.Split(o.Delimiters.Repeat).Any(a => a.Trim() == "Q")
                   || specimenKind.Equals("QC", StringComparison.OrdinalIgnoreCase)
                   || specimenKind.Equals("CONTROL", StringComparison.OrdinalIgnoreCase);
        if (!isQc) return (sid, false, null, null);
        // Control sample: "control name" or "control name^lot number" (manual E-11)
        return (sid, true, sid, Nullify(o.Component(3, 1)));
    }

    private NormalizedResult ParseResult(AstmRecord r, E411ProtocolVariant v, string? orderTimestamp)
    {
        string code, dilution, predilution;
        if (v == E411ProtocolVariant.Cobas)
        {
            // ^^^<ApplicationCode>/<Dilution>/<pre-dilution>
            var parts = r.Component(3, 3).Split('/');
            code = parts[0].Trim();
            dilution = parts.Length > 1 ? parts[1].Trim() : "";
            predilution = parts.Length > 2 ? parts[2].Trim() : "";
        }
        else
        {
            // ^^^<ApplicationCode>^<Dilution>^<pre-dilution>
            code = r.Component(3, 3).Trim();
            dilution = r.Component(3, 4).Trim();
            predilution = r.Component(3, 5).Trim();
        }

        var rawValue = r.Text(4);
        var primary = r.Component(4, 0).Trim();
        var secondary = Nullify(r.Component(4, 1).Trim());
        string? comparator = null;
        var numericText = primary;
        if (primary.StartsWith('<') || primary.StartsWith('>'))
        {
            comparator = primary[..1];
            numericText = primary[1..].Trim();
        }
        decimal? numeric = decimal.TryParse(numericText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
        // Qualitative tests send 1 / 0 / -1 followed by a cut-off index; whether a test is qualitative is
        // decided by the approved mapping, so both forms are kept.
        string? qualitative = secondary is not null && primary is "1" or "0" or "-1" ? primary : null;

        var flags = new List<string>();
        var abnormal = r.Text(7).Trim();
        if (abnormal.Length > 0) flags.Add(abnormal);
        if (predilution is "pre-diluted" or "1") flags.Add("pre-diluted");

        var status = Nullify(r.Text(9).Trim());
        var runText = v == E411ProtocolVariant.Elecsys ? FirstNonEmpty(r.Text(13), r.Text(12)) : FirstNonEmpty(orderTimestamp, r.Text(13));
        var refRange = r.Field(6).Length > 0 ? r.Text(6).Replace(r.Delimiters.Component, '-').Trim('-') : null;

        return new NormalizedResult(
            InstrumentTestCode: code,
            RawValue: rawValue,
            Units: Nullify(r.Text(5).Trim()),
            Comparator: comparator,
            NumericValue: numeric,
            QualitativeValue: qualitative,
            SecondaryValue: secondary,
            InstrumentFlags: flags,
            ReferenceRangeText: Nullify(refRange),
            RunAt: ParseTimestamp(runText),
            Dilution: Nullify(dilution),
            IsRerun: string.Equals(status, "C", StringComparison.Ordinal),
            OperatorId: Nullify(r.Text(11).Trim()),
            ResultStatus: status);
    }

    private DateTimeOffset? ParseTimestamp(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return DateTime.TryParseExact(s, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? new DateTimeOffset(dt, instrumentUtcOffset)
            : null;
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
