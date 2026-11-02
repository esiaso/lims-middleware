namespace Lims.Instruments.Astm;

/// <summary>Delimiters declared in the H record (field 2). Defaults are | \ ^ &amp;.</summary>
public sealed record AstmDelimiters(char Field, char Repeat, char Component, char Escape)
{
    public static readonly AstmDelimiters Default = new('|', '\\', '^', '&');

    public static AstmDelimiters FromHeader(string headerRecord)
    {
        if (headerRecord.Length < 5 || headerRecord[0] != 'H') return Default;
        return new AstmDelimiters(headerRecord[1], headerRecord[2], headerRecord[3], headerRecord[4]);
    }
}

/// <summary>One ASTM E1394 record split into fields. Field numbers follow the standard (1 = record type).</summary>
public sealed class AstmRecord
{
    private readonly string[] _fields;
    public AstmDelimiters Delimiters { get; }
    public char Type { get; }
    public string Raw { get; }

    public AstmRecord(string raw, AstmDelimiters d)
    {
        Raw = raw;
        Delimiters = d;
        _fields = raw.Split(d.Field);
        Type = raw.Length > 0 ? char.ToUpperInvariant(raw[0]) : '\0';
    }

    public int FieldCount => _fields.Length;

    /// <summary>Raw (escaped) field text; empty when absent.</summary>
    public string Field(int astmFieldNumber) =>
        astmFieldNumber >= 1 && astmFieldNumber <= _fields.Length ? _fields[astmFieldNumber - 1] : string.Empty;

    public string[] Components(int astmFieldNumber) => Field(astmFieldNumber).Split(Delimiters.Component);

    public string[] Repeats(int astmFieldNumber) => Field(astmFieldNumber).Split(Delimiters.Repeat);

    public string Component(int astmFieldNumber, int componentIndex)
    {
        var c = Components(astmFieldNumber);
        return componentIndex < c.Length ? Unescape(c[componentIndex]) : string.Empty;
    }

    public string Text(int astmFieldNumber) => Unescape(Field(astmFieldNumber));

    /// <summary>Escape sequences per E1394: &amp;F&amp; field, &amp;S&amp; component, &amp;R&amp; repeat, &amp;E&amp; escape.</summary>
    public string Unescape(string s)
    {
        var e = Delimiters.Escape;
        if (s.IndexOf(e) < 0) return s;
        return s.Replace($"{e}F{e}", Delimiters.Field.ToString(), StringComparison.Ordinal)
                .Replace($"{e}S{e}", Delimiters.Component.ToString(), StringComparison.Ordinal)
                .Replace($"{e}R{e}", Delimiters.Repeat.ToString(), StringComparison.Ordinal)
                .Replace($"{e}E{e}", e.ToString(), StringComparison.Ordinal);
    }

    public static IReadOnlyList<AstmRecord> SplitMessage(string message)
    {
        var lines = message.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r')
            .Split('\r', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        var header = lines.FirstOrDefault(l => l.StartsWith('H'));
        var d = header is null ? AstmDelimiters.Default : AstmDelimiters.FromHeader(header);
        return lines.Select(l => new AstmRecord(l, d)).ToList();
    }
}
