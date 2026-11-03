using System.Text.Json;
using Lims.Contracts;
using Lims.Instruments;
using Lims.Instruments.Registration;

namespace Lims.Capture;

/// <summary>
/// Turns captured messages into parser regression tests. Layout: tests/captures/&lt;parser id&gt;/NNNN-rx.msg, with an optional
/// NNNN-rx.expected.json beside each. The unit tests parse every .msg file and compare with its .expected.json when present.
/// A registered analyzer (generic adapter) has its own folder, tests/captures/&lt;analyzer id&gt;/, holding its approved
/// definition as definition.json (as GET /api/analyzers returns it): replays use that message layout.
/// </summary>
public static class Replay
{
    /// <summary>Instrument clock offset used for every replay, so timestamps in snapshots are stable (Kenya, EAT).</summary>
    public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(3);

    public static IMessageParser Parser(string parserId) =>
        ParserRegistry.Create(new InstrumentConfig { Id = "replay", Parser = parserId, UtcOffset = UtcOffset })
        ?? throw new ArgumentException($"unknown parser '{parserId}'. Known: {string.Join(", ", ParserRegistry.KnownParsers)}");

    public const string DefinitionFile = "definition.json";

    /// <summary>A registered analyzer's parser, with the message layout of its definition.</summary>
    public static IMessageParser Parser(AnalyzerDefinition d)
    {
        var errors = d.Profile.Validate(d.IsHl7);
        if (errors.Count > 0) throw new ArgumentException($"definition for '{d.InstrumentId}' is not usable: {string.Join("; ", errors)}");
        var cfg = d.ToConfig(Guid.Empty, 0, enabled: true);
        cfg.Id = "replay";
        cfg.UtcOffset = UtcOffset;
        return ParserRegistry.Create(cfg)!;
    }

    public static AnalyzerDefinition LoadDefinition(string path)
    {
        try { return AnalyzerDefinition.FromJson(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new ArgumentException($"{path} is not an analyzer definition: {ex.Message}"); }
    }

    /// <summary>The parser for a captures folder: its definition.json when there is one, else the parser named by the folder.</summary>
    public static IMessageParser ForFolder(string dir)
    {
        var def = Path.Combine(dir, DefinitionFile);
        return File.Exists(def) ? Parser(LoadDefinition(def)) : Parser(Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)));
    }

    public static string Snapshot(string parserId, byte[] message) => Snapshot(Parser(parserId), message);

    public static string Snapshot(IMessageParser parser, byte[] message)
    {
        var outcome = parser.Parse("replay", message, "replay");
        return JsonSerializer.Serialize(new { outcome.Success, outcome.Error, outcome.Messages }, Options);
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string ProtocolOf(string parserId) => ParserRegistry.IsHl7(parserId) ? "hl7" : "astm";
}
