namespace Lims.Platform;

/// <summary>
/// Accession format (design 5.1, ILLUSTRATIVE until the lab confirms label lengths per analyzer):
/// YYMMDD + 5-digit daily sequence + mod-10 check digit. Display "26110200042-7"; barcode "261102000427".
/// 12 digits fits the e411 (22) and c111 (23) sample-ID limits.
/// </summary>
public static class AccessionNumber
{
    public static (string Display, string Barcode) Format(DateOnly day, int sequence)
    {
        if (sequence is < 1 or > 99999) throw new ArgumentOutOfRangeException(nameof(sequence));
        var body = $"{day:yyMMdd}{sequence:D5}";
        var check = CheckDigit(body);
        return ($"{body}-{check}", $"{body}{check}");
    }

    /// <summary>Luhn mod-10 check digit, catches all single-digit errors and most transpositions.</summary>
    public static int CheckDigit(string digits)
    {
        var sum = 0;
        var doubleIt = true;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (d is < 0 or > 9) throw new FormatException("Accession body must be digits.");
            if (doubleIt) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            doubleIt = !doubleIt;
        }
        return (10 - sum % 10) % 10;
    }

    /// <summary>Normalizes a scanned or instrument-sent ID to barcode form; null when the check digit is wrong.</summary>
    public static string? NormalizeBarcode(string? sampleId)
    {
        if (string.IsNullOrWhiteSpace(sampleId)) return null;
        var s = sampleId.Trim().Replace("-", "", StringComparison.Ordinal);
        if (s.Length != 12 || !s.All(char.IsAsciiDigit)) return null;
        return CheckDigit(s[..11]) == s[11] - '0' ? s : null;
    }
}
