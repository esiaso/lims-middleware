using System.Globalization;
using System.Text;

namespace Lims.Instruments.Generic;

/// <summary>How orders reach a registered analyzer.</summary>
public enum WorklistMode
{
    /// <summary>Results only: the analyzer is loaded by hand from the printed load list.</summary>
    None,
    /// <summary>The analyzer asks for a tube's tests when it reads the barcode (ASTM Q record, HL7 order query).</summary>
    HostQuery,
    /// <summary>The middleware sends the order to the analyzer on request ("Resend order").</summary>
    Push,
    /// <summary>Both of the above.</summary>
    Both,
}

/// <summary>
/// A position in a message: record or segment, field number and optional component, written the usual way
/// ("R-4", "R-3.4", "OBX-3.1"). Field and component numbers are 1-based, as the standards and vendor manuals count.
/// For ASTM the record is the record type letter (H, P, O, R, C, Q, L).
/// </summary>
public sealed record FieldRef(string Record, int Field, int? Component)
{
    public override string ToString() => Component is null ? $"{Record}-{Field}" : $"{Record}-{Field}.{Component}";

    public static bool TryParse(string? text, out FieldRef? value, out string? error)
    {
        value = null;
        error = null;
        var s = text?.Trim().ToUpperInvariant() ?? "";
        var dash = s.IndexOf('-');
        if (dash <= 0 || dash == s.Length - 1) { error = $"'{text}' is not a field position like R-4 or OBX-3.1"; return false; }
        var record = s[..dash];
        if (!record.All(char.IsAsciiLetterOrDigit) || record.Length > 3) { error = $"'{record}' is not a record or segment name"; return false; }
        var rest = s[(dash + 1)..].Split('.');
        if (rest.Length > 2 || !int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out var field) || field < 1 || field > 999)
        {
            error = $"'{text}' has no valid field number";
            return false;
        }
        int? component = null;
        if (rest.Length == 2)
        {
            if (!int.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out var c) || c < 1 || c > 99)
            {
                error = $"'{text}' has no valid component number";
                return false;
            }
            component = c;
        }
        value = new FieldRef(record, field, component);
        return true;
    }

    public static FieldRef Parse(string text) =>
        TryParse(text, out var v, out var e) ? v! : throw new FormatException(e);

    /// <summary>Parses an optional reference: blank means "not used".</summary>
    public static FieldRef? ParseOptional(string? text) => string.IsNullOrWhiteSpace(text) ? null : Parse(text);
}

/// <summary>
/// Message layout of an analyzer that has no built-in adapter. A Lab Manager fills it in from the vendor's host
/// interface manual; defaults follow ASTM E1394 and HL7 v2.3.1 as most analyzers use them.
/// Everything set here is CONFIGURED, not interface-tested, until the site verification is signed for the analyzer.
/// </summary>
public sealed class GenericAdapterProfile
{
    /// <summary>Text encoding of the analyzer's messages: ascii, utf-8 or latin1.</summary>
    public string Encoding { get; set; } = "ascii";
    public WorklistMode Worklist { get; set; } = WorklistMode.None;
    /// <summary>Instrument codes that are not patient results (run modes, histograms, comments): skipped, never filed.</summary>
    public List<string> IgnoredTestCodes { get; set; } = new();
    /// <summary>A sample ID starting with one of these is a control (QC), not a patient tube.</summary>
    public List<string> QcSampleIdPrefixes { get; set; } = new();
    /// <summary>Result status values that mean "rerun" (ASTM R-9 or HL7 OBX-11). Default C (corrected).</summary>
    public List<string> RerunStatusValues { get; set; } = ["C"];
    /// <summary>Abnormal flag values that mean "normal" and are not kept as flags.</summary>
    public List<string> NormalFlagValues { get; set; } = ["N"];
    /// <summary>Timestamp formats tried in order (.NET custom format strings).</summary>
    public List<string> TimestampFormats { get; set; } = ["yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd"];
    /// <summary>Sender name the middleware puts in its messages (ASTM H-5, HL7 MSH-3).</summary>
    public string HostName { get; set; } = "LIS";
    /// <summary>Receiver name (ASTM H-10, HL7 MSH-5). Some analyzers check it.</summary>
    public string ReceiverName { get; set; } = "";
    /// <summary>Most tests the analyzer accepts in one order.</summary>
    public int MaxTestsPerOrder { get; set; } = 50;

    // ------------------------------------------------------------------ ASTM E1394
    public string AstmSampleId { get; set; } = "O-3";
    /// <summary>Tried when the sample ID field is empty (O-4 is the instrument specimen ID).</summary>
    public string? AstmSampleIdFallback { get; set; } = "O-4.1";
    public string AstmTestCode { get; set; } = "R-3.4";
    public string AstmValue { get; set; } = "R-4";
    public string? AstmUnits { get; set; } = "R-5";
    public string? AstmReferenceRange { get; set; } = "R-6";
    public string? AstmAbnormalFlag { get; set; } = "R-7";
    public string? AstmResultStatus { get; set; } = "R-9";
    public string? AstmOperator { get; set; } = "R-11";
    public string? AstmCompletedAt { get; set; } = "R-13";
    /// <summary>An O record whose action code (O-12) is this value is a control (QC).</summary>
    public string? AstmQcActionCode { get; set; } = "Q";
    public string AstmQuerySampleId { get; set; } = "Q-3.2";
    /// <summary>How one test is written in the order's universal test ID (O-5); {code} is the instrument code.</summary>
    public string AstmTestCodeTemplate { get; set; } = "^^^{code}";

    // ------------------------------------------------------------------ HL7 v2
    public string Hl7Version { get; set; } = "2.3.1";
    public string Hl7SampleId { get; set; } = "OBR-3";
    public string? Hl7SampleIdFallback { get; set; } = "OBR-2";
    public string Hl7TestCode { get; set; } = "OBX-3.1";
    public string Hl7Value { get; set; } = "OBX-5";
    public string? Hl7Units { get; set; } = "OBX-6.1";
    public string? Hl7ReferenceRange { get; set; } = "OBX-7";
    public string? Hl7AbnormalFlag { get; set; } = "OBX-8";
    public string? Hl7ResultStatus { get; set; } = "OBX-11";
    public string? Hl7Operator { get; set; } = "OBX-16";
    public string? Hl7CompletedAt { get; set; } = "OBX-14";
    /// <summary>MSH-11 processing ID "Q" marks a control (QC) message.</summary>
    public bool Hl7QcWhenProcessingIdQ { get; set; } = true;
    /// <summary>Message type the analyzer sends to ask for a tube's tests, for example ORM^O01 or QRY^Q02.</summary>
    public string Hl7QueryMessage { get; set; } = "ORM^O01";
    public string Hl7QuerySampleId { get; set; } = "ORC-3";
    /// <summary>Message type of the middleware's answer to an order query.</summary>
    public string Hl7QueryReplyMessage { get; set; } = "ORR^O02";
    /// <summary>Message type the middleware sends to push an order.</summary>
    public string Hl7OrderMessage { get; set; } = "ORM^O01";

    public bool AnswersQueries => Worklist is WorklistMode.HostQuery or WorklistMode.Both;
    public bool PushesOrders => Worklist is WorklistMode.Push or WorklistMode.Both;

    public static readonly string[] Encodings = ["ascii", "utf-8", "latin1"];

    public System.Text.Encoding TextEncoding() => Encoding.Trim().ToLowerInvariant() switch
    {
        "utf-8" or "utf8" => new UTF8Encoding(false),
        "latin1" or "iso-8859-1" => System.Text.Encoding.Latin1,
        _ => System.Text.Encoding.ASCII,
    };

    /// <summary>Every problem with the profile for the given protocol; empty when it can be used.</summary>
    public IReadOnlyList<string> Validate(bool hl7)
    {
        var errors = new List<string>();
        if (!Encodings.Contains(Encoding.Trim().ToLowerInvariant())) errors.Add($"Encoding must be one of {string.Join(", ", Encodings)}");
        if (MaxTestsPerOrder is < 1 or > 999) errors.Add("Most tests per order must be between 1 and 999");
        if (TimestampFormats.Count == 0) errors.Add("At least one timestamp format is needed");
        foreach (var f in TimestampFormats)
        {
            try { _ = new DateTime(2026, 1, 2, 3, 4, 5).ToString(f, CultureInfo.InvariantCulture); }
            catch (FormatException) { errors.Add($"'{f}' is not a valid timestamp format"); }
        }
        if (string.IsNullOrWhiteSpace(HostName)) errors.Add("Host name is required");

        void Ref(string label, string? value, bool required, params string[] records)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required) errors.Add($"{label} is required");
                return;
            }
            if (!FieldRef.TryParse(value, out var r, out var e)) { errors.Add($"{label}: {e}"); return; }
            if (records.Length > 0 && !records.Contains(r!.Record))
                errors.Add($"{label}: must point into {string.Join(" or ", records)}, not {r!.Record}");
        }

        if (hl7)
        {
            Ref("Sample ID", Hl7SampleId, true, "OBR", "ORC", "PID", "SPM", "PV1");
            Ref("Sample ID fallback", Hl7SampleIdFallback, false, "OBR", "ORC", "PID", "SPM", "PV1");
            Ref("Test code", Hl7TestCode, true, "OBX");
            Ref("Value", Hl7Value, true, "OBX");
            Ref("Units", Hl7Units, false, "OBX");
            Ref("Reference range", Hl7ReferenceRange, false, "OBX");
            Ref("Abnormal flag", Hl7AbnormalFlag, false, "OBX");
            Ref("Result status", Hl7ResultStatus, false, "OBX");
            Ref("Operator", Hl7Operator, false, "OBX", "OBR");
            Ref("Completed at", Hl7CompletedAt, false, "OBX", "OBR");
            Ref("Query sample ID", Hl7QuerySampleId, AnswersQueries);
            foreach (var (label, type) in new[] { ("Query message", Hl7QueryMessage), ("Query reply message", Hl7QueryReplyMessage), ("Order message", Hl7OrderMessage) })
                if (!IsMessageType(type)) errors.Add($"{label} must look like ORM^O01");
            if (!Hl7Version.StartsWith("2.", StringComparison.Ordinal)) errors.Add("HL7 version must be 2.x");
        }
        else
        {
            Ref("Sample ID", AstmSampleId, true, "O", "P");
            Ref("Sample ID fallback", AstmSampleIdFallback, false, "O", "P");
            Ref("Test code", AstmTestCode, true, "R");
            Ref("Value", AstmValue, true, "R");
            Ref("Units", AstmUnits, false, "R");
            Ref("Reference range", AstmReferenceRange, false, "R");
            Ref("Abnormal flag", AstmAbnormalFlag, false, "R");
            Ref("Result status", AstmResultStatus, false, "R");
            Ref("Operator", AstmOperator, false, "R");
            Ref("Completed at", AstmCompletedAt, false, "R", "O", "H");
            Ref("Query sample ID", AstmQuerySampleId, AnswersQueries, "Q");
            if (!AstmTestCodeTemplate.Contains("{code}", StringComparison.Ordinal)) errors.Add("The test code template must contain {code}");
        }
        return errors;
    }

    private static bool IsMessageType(string? s)
    {
        var parts = (s ?? "").Split('^');
        return parts.Length == 2 && parts.All(p => p.Length == 3 && p.All(char.IsAsciiLetterOrDigit));
    }
}
