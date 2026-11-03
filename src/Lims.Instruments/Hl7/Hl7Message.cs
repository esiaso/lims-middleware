using System.Text;

namespace Lims.Instruments.Hl7;

/// <summary>Delimiters declared in MSH-1 and MSH-2. Defaults are | ^ ~ \ &amp;.</summary>
public sealed record Hl7Delimiters(char Field, char Component, char Repeat, char Escape, char SubComponent)
{
    public static readonly Hl7Delimiters Default = new('|', '^', '~', '\\', '&');
}

/// <summary>One HL7 v2 segment. Field numbers follow the standard: for MSH, MSH-1 is the field separator itself.</summary>
public sealed class Hl7Segment
{
    private readonly string[] _fields;
    public string Name { get; }
    public Hl7Delimiters Delimiters { get; }
    public string Raw { get; }

    public Hl7Segment(string raw, Hl7Delimiters d)
    {
        Raw = raw;
        Delimiters = d;
        _fields = raw.Split(d.Field);
        Name = _fields[0];
    }

    /// <summary>Raw (escaped) field text; empty when absent.</summary>
    public string Field(int n)
    {
        if (Name == "MSH")
        {
            if (n == 1) return Delimiters.Field.ToString();
            n -= 1; // MSH-2 is _fields[1]
        }
        return n >= 1 && n < _fields.Length ? _fields[n] : string.Empty;
    }

    public string[] Repeats(int n) => Field(n).Split(Delimiters.Repeat);

    public string Component(int n, int componentIndex)
    {
        var c = Field(n).Split(Delimiters.Component);
        return componentIndex < c.Length ? Unescape(c[componentIndex]) : string.Empty;
    }

    public string Text(int n) => Unescape(Field(n));

    /// <summary>Escape sequences per HL7 2.3.1 and the Mindray protocol section 3.</summary>
    public string Unescape(string s)
    {
        var e = Delimiters.Escape;
        if (s.IndexOf(e) < 0) return s;
        return s.Replace($"{e}F{e}", Delimiters.Field.ToString(), StringComparison.Ordinal)
                .Replace($"{e}S{e}", Delimiters.Component.ToString(), StringComparison.Ordinal)
                .Replace($"{e}T{e}", Delimiters.SubComponent.ToString(), StringComparison.Ordinal)
                .Replace($"{e}R{e}", Delimiters.Repeat.ToString(), StringComparison.Ordinal)
                .Replace($"{e}.br{e}", "\r", StringComparison.Ordinal)
                .Replace($"{e}E{e}", e.ToString(), StringComparison.Ordinal);
    }

    public static string Escape(string s, Hl7Delimiters d)
    {
        var e = d.Escape;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == e) sb.Append(e).Append('E').Append(e);
            else if (c == d.Field) sb.Append(e).Append('F').Append(e);
            else if (c == d.Component) sb.Append(e).Append('S').Append(e);
            else if (c == d.SubComponent) sb.Append(e).Append('T').Append(e);
            else if (c == d.Repeat) sb.Append(e).Append('R').Append(e);
            else if (c == '\r') sb.Append(e).Append(".br").Append(e);
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

public sealed class Hl7Message
{
    public IReadOnlyList<Hl7Segment> Segments { get; }
    public Hl7Segment Msh => Segments[0];
    public string MessageType => Msh.Component(9, 0);
    public string TriggerEvent => Msh.Component(9, 1);
    public string ControlId => Msh.Text(10);
    public string ProcessingId => Msh.Text(11);
    public string Version => Msh.Text(12);

    private Hl7Message(IReadOnlyList<Hl7Segment> segments) => Segments = segments;

    public IEnumerable<Hl7Segment> All(string name) => Segments.Where(s => s.Name == name);
    public Hl7Segment? First(string name) => Segments.FirstOrDefault(s => s.Name == name);

    /// <summary>Parses UTF-8 text with CR (or CR LF / LF) segment terminators. Returns null when there is no MSH segment first.</summary>
    public static Hl7Message? TryParse(string text, out string? error)
    {
        error = null;
        var lines = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r')
            .Split('\r', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("MSH", StringComparison.Ordinal) || lines[0].Length < 8)
        {
            error = "message does not start with an MSH segment";
            return null;
        }
        var m = lines[0];
        var d = new Hl7Delimiters(m[3], m[4], m[5], m[6], m[7]);
        return new Hl7Message(lines.Select(l => new Hl7Segment(l, d)).ToList());
    }
}
