using System.Text.Json;
using Lims.Contracts;
using Lims.Instruments;

namespace Lims.Capture;

/// <summary>
/// Turns captured messages into parser regression tests. Layout: tests/captures/&lt;parser id&gt;/NNNN-rx.msg, with an optional
/// NNNN-rx.expected.json beside each. The unit tests parse every .msg file and compare with its .expected.json when present.
/// </summary>
public static class Replay
{
    /// <summary>Instrument clock offset used for every replay, so timestamps in snapshots are stable (Kenya, EAT).</summary>
    public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(3);

    public static IMessageParser Parser(string parserId) =>
        ParserRegistry.Create(new InstrumentConfig { Id = "replay", Parser = parserId, UtcOffset = UtcOffset })
        ?? throw new ArgumentException($"unknown parser '{parserId}'. Known: {string.Join(", ", ParserRegistry.KnownParsers)}");

    public static string Snapshot(string parserId, byte[] message)
    {
        var outcome = Parser(parserId).Parse("replay", message, "replay");
        return JsonSerializer.Serialize(new { outcome.Success, outcome.Error, outcome.Messages }, Options);
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string ProtocolOf(string parserId) => ParserRegistry.IsHl7(parserId) ? "hl7" : "astm";
}
