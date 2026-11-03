using System.Globalization;
using Lims.Contracts;
using Lims.Instruments.Astm;

namespace Lims.Instruments.Generic;

/// <summary>
/// Configurable ASTM E1394 parser for analyzers registered from the screens. Field positions come from the
/// analyzer's <see cref="GenericAdapterProfile"/>. CONFIGURED, NOT interface-tested: an analyzer registered this way
/// is "unverified" until its site verification is signed.
/// </summary>
public sealed class GenericAstmParser(GenericAdapterProfile profile, TimeSpan instrumentUtcOffset) : IMessageParser
{
    public const string Id = "generic.astm";
    public string ParserId => Id;

    private readonly FieldRef _sampleId = FieldRef.Parse(profile.AstmSampleId);
    private readonly FieldRef? _sampleIdFallback = FieldRef.ParseOptional(profile.AstmSampleIdFallback);
    private readonly FieldRef _testCode = FieldRef.Parse(profile.AstmTestCode);
    private readonly FieldRef _value = FieldRef.Parse(profile.AstmValue);
    private readonly FieldRef? _units = FieldRef.ParseOptional(profile.AstmUnits);
    private readonly FieldRef? _range = FieldRef.ParseOptional(profile.AstmReferenceRange);
    private readonly FieldRef? _flag = FieldRef.ParseOptional(profile.AstmAbnormalFlag);
    private readonly FieldRef? _status = FieldRef.ParseOptional(profile.AstmResultStatus);
    private readonly FieldRef? _operator = FieldRef.ParseOptional(profile.AstmOperator);
    private readonly FieldRef? _completedAt = FieldRef.ParseOptional(profile.AstmCompletedAt);
    private readonly FieldRef? _querySampleId = FieldRef.ParseOptional(profile.AstmQuerySampleId);
    private readonly HashSet<string> _ignored = new(profile.IgnoredTestCodes.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);

    public ParseOutcome Parse(string instrumentId, byte[] raw, string correlationId)
    {
        var text = profile.TextEncoding().GetString(raw);
        var records = AstmRecord.SplitMessage(text);
        if (records.Count == 0 || records[0].Type != 'H') return ParseOutcome.Fail("message does not start with an H record");

        var ctx = new Dictionary<char, AstmRecord> { ['H'] = records[0] };
        var messages = new List<NormalizedInstrumentMessage>();
        var results = new List<NormalizedResult>();
        string? sampleId = null;
        var isQc = false;
        var inOrder = false;

        void FlushOrder()
        {
            if (!inOrder) return;
            messages.Add(new NormalizedInstrumentMessage(instrumentId, isQc ? MessageKind.Qc : MessageKind.PatientResult,
                sampleId, results.ToList(), correlationId, isQc ? sampleId : null, null));
            results.Clear();
            inOrder = false;
        }

        foreach (var r in records.Skip(1))
        {
            switch (r.Type)
            {
                case 'P':
                    FlushOrder();
                    ctx['P'] = r;
                    ctx.Remove('O');
                    break;
                case 'O':
                    FlushOrder();
                    ctx['O'] = r;
                    inOrder = true;
                    sampleId = Nullify(Read(ctx, _sampleId)) ?? (_sampleIdFallback is null ? null : Nullify(Read(ctx, _sampleIdFallback)));
                    isQc = IsControl(r, sampleId);
                    break;
                case 'R':
                    if (!inOrder) return ParseOutcome.Fail("R record without a preceding O record");
                    ctx['R'] = r;
                    var parsed = ParseResult(ctx);
                    if (parsed is not null) results.Add(parsed);
                    break;
                case 'C':
                    // C|1|I|<code>^<text>|I : instrument comment on the preceding result, kept as a flag.
                    if (results.Count == 0) break;
                    var code = r.Component(4, 0).Trim();
                    if (code.Length > 0) results[^1] = results[^1] with { InstrumentFlags = results[^1].InstrumentFlags.Append($"comment:{code}").ToList() };
                    break;
                case 'Q':
                    FlushOrder();
                    ctx['Q'] = r;
                    messages.Add(ParseQuery(instrumentId, ctx, correlationId));
                    break;
                case 'L':
                    FlushOrder();
                    break;
                default:
                    break; // M (manufacturer) and S (scientific) records carry no patient results
            }
        }
        FlushOrder();
        return messages.Count == 0 ? ParseOutcome.Fail("message carries no query or order records") : ParseOutcome.Ok(messages);
    }

    private bool IsControl(AstmRecord order, string? sampleId)
    {
        if (profile.AstmQcActionCode is { Length: > 0 } qc && order.Repeats(12).Any(a => a.Trim().Equals(qc.Trim(), StringComparison.OrdinalIgnoreCase)))
            return true;
        return sampleId is not null && profile.QcSampleIdPrefixes.Any(p => p.Length > 0 && sampleId.StartsWith(p.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private NormalizedInstrumentMessage ParseQuery(string instrumentId, Dictionary<char, AstmRecord> ctx, string correlationId)
    {
        var q = ctx['Q'];
        var sid = _querySampleId is null ? null : Nullify(Read(ctx, _querySampleId));
        sid ??= Nullify(q.Component(3, 1)) ?? Nullify(q.Component(3, 0));
        var status = q.Text(13).Trim().ToUpperInvariant();
        return new NormalizedInstrumentMessage(instrumentId, MessageKind.Query, sid, [], correlationId,
            Query: new QueryDetails(null, null, null, null, null, IsCancel: status == "A"));
    }

    private NormalizedResult? ParseResult(Dictionary<char, AstmRecord> ctx)
    {
        var code = Read(ctx, _testCode).Trim();
        if (code.Length == 0 || _ignored.Contains(code)) return null;
        var status = Nullify(Read(ctx, _status).Trim().ToUpperInvariant());
        var value = status == "X" ? "" : Read(ctx, _value).Trim();
        var (comparator, numeric) = GenericValues.Numeric(value);

        var flags = new List<string>();
        var abnormal = Read(ctx, _flag).Trim();
        if (abnormal.Length > 0 && !profile.NormalFlagValues.Contains(abnormal, StringComparer.OrdinalIgnoreCase))
            flags.AddRange(abnormal.Split(ctx['R'].Delimiters.Repeat).Select(f => f.Trim()).Where(f => f.Length > 0));
        if (status == "X") flags.Add("not completed");

        var range = Read(ctx, _range);
        string? refRange = null;
        if (range.Length > 0)
        {
            var parts = range.Split(ctx['R'].Delimiters.Repeat, ctx['R'].Delimiters.Component).Select(p => p.Trim()).Where(p => p.Length > 0).Take(2).ToArray();
            refRange = parts.Length == 2 ? $"{parts[0]}-{parts[1]}" : parts.FirstOrDefault();
        }

        return new NormalizedResult(
            InstrumentTestCode: code,
            RawValue: value,
            Units: Nullify(Read(ctx, _units).Trim()),
            Comparator: comparator,
            NumericValue: numeric,
            QualitativeValue: null,
            SecondaryValue: null,
            InstrumentFlags: flags,
            ReferenceRangeText: refRange,
            RunAt: GenericValues.Timestamp(Nullify(Read(ctx, _completedAt)) ?? Nullify(ctx['H'].Text(14)), profile.TimestampFormats, instrumentUtcOffset),
            Dilution: null,
            IsRerun: status is not null && profile.RerunStatusValues.Contains(status, StringComparer.OrdinalIgnoreCase),
            OperatorId: Nullify(Read(ctx, _operator).Trim()),
            ResultStatus: status);
    }

    private static string Read(Dictionary<char, AstmRecord> ctx, FieldRef? f)
    {
        if (f is null || !ctx.TryGetValue(f.Record[0], out var r)) return "";
        return f.Component is { } c ? r.Component(f.Field, c - 1) : r.Text(f.Field);
    }

    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Messages the middleware sends to a generic ASTM analyzer: host query replies and pushed orders.</summary>
public static class GenericAstmMessages
{
    /// <summary>
    /// One order for one sample, E1394 layout: O-3 sample ID, O-5 tests (repeat-separated), O-6 priority, O-12 action
    /// code A (add), O-26 report type O. With no order, the reply carries H and L only: the analyzer runs nothing.
    /// </summary>
    public static string Order(GenericAdapterProfile p, string sampleId, InstrumentOrder? order, DateTimeOffset now)
    {
        var header = $"H|\\^&|||{Esc(p.HostName)}|||||{Esc(p.ReceiverName)}||P|1|{now:yyyyMMddHHmmss}";
        if (order is null || order.InstrumentTestCodes.Count == 0) return $"{header}\rL|1|N\r";
        var o = new string[26];
        Array.Fill(o, "");
        o[0] = "O";
        o[1] = "1";
        o[2] = Esc(sampleId);
        o[4] = string.Join("\\", order.InstrumentTestCodes.Distinct().Take(p.MaxTestsPerOrder)
            .Select(c => p.AstmTestCodeTemplate.Replace("{code}", Esc(c), StringComparison.Ordinal)));
        o[5] = order.Priority == "stat" ? "S" : "R";
        o[6] = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        o[11] = "A";
        o[25] = "O";
        return string.Join("\r", header, "P|1", string.Join("|", o), "L|1|N") + "\r";
    }

    /// <summary>Escapes delimiter characters in data per E1394 (default delimiters).</summary>
    public static string Esc(string s) => s.Replace("&", "&E&", StringComparison.Ordinal).Replace("|", "&F&", StringComparison.Ordinal)
        .Replace("\\", "&R&", StringComparison.Ordinal).Replace("^", "&S&", StringComparison.Ordinal);
}

internal static class GenericValues
{
    public static (string? Comparator, decimal? Numeric) Numeric(string value)
    {
        string? comparator = null;
        var text = value;
        foreach (var c in new[] { "<=", ">=", "<", ">" })
        {
            if (!value.StartsWith(c, StringComparison.Ordinal)) continue;
            comparator = c[..1];
            text = value[c.Length..].Trim();
            break;
        }
        return (comparator, decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null);
    }

    public static DateTimeOffset? Timestamp(string? s, IReadOnlyList<string> formats, TimeSpan offset)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var f = formats.ToArray();
        if (DateTime.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return new DateTimeOffset(dt, offset);
        // HL7 TS may carry fractional seconds or a zone (20261103101500.123+0300): drop them, the instrument clock offset applies.
        var cut = s.Length > 8 ? s.IndexOfAny(['+', '-', '.'], 8) : -1;
        return cut > 0 && DateTime.TryParseExact(s[..cut], f, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)
            ? new DateTimeOffset(dt, offset)
            : null;
    }
}
