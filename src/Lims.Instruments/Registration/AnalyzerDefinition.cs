using System.IO.Ports;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lims.Instruments.Astm;
using Lims.Instruments.Generic;
using Lims.Platform;

namespace Lims.Instruments.Registration;

/// <summary>
/// An analyzer with no built-in adapter, as a Lab Manager registers it: identity, transport, link timers and the
/// message layout read by the generic ASTM or HL7 adapter. Stored as JSON in instruments.analyzer_definition.
/// </summary>
public sealed partial class AnalyzerDefinition
{
    public string InstrumentId { get; set; } = "";
    public string Model { get; set; } = "";
    public string? SoftwareVersion { get; set; }
    /// <summary>"astm" (E1381/E1394) or "hl7" (v2 over MLLP).</summary>
    public string Protocol { get; set; } = "astm";
    public TimeSpan UtcOffset { get; set; } = TimeSpan.FromHours(3);
    public TransportOptions Transport { get; set; } = new();
    public AstmLinkOptions Link { get; set; } = new();
    public GenericAdapterProfile Profile { get; set; } = new();

    public bool IsHl7 => Protocol == "hl7";
    public string ParserId => IsHl7 ? GenericHl7Parser.Id : GenericAstmParser.Id;

    public static readonly JsonSerializerOptions JsonOptions = new(Json.Options) { Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static AnalyzerDefinition FromJson(string json) =>
        JsonSerializer.Deserialize<AnalyzerDefinition>(json, JsonOptions) ?? throw new JsonException("empty analyzer definition");

    public InstrumentConfig ToConfig(Guid definitionId, int version, bool enabled) => new()
    {
        Id = InstrumentId,
        Model = Model,
        Parser = ParserId,
        Enabled = enabled,
        HostQuery = Profile.AnswersQueries,
        SoftwareVersion = SoftwareVersion,
        UtcOffset = UtcOffset,
        Transport = Transport,
        Link = Link,
        Generic = Profile,
        DefinitionId = definitionId,
        DefinitionVersion = version,
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,39}$")]
    private static partial Regex IdPattern();

    /// <summary>Every reason the definition cannot be proposed; empty when it can. <paramref name="others"/> is every other known analyzer.</summary>
    public IReadOnlyList<string> Validate(IEnumerable<InstrumentConfig> others, IEnumerable<string> configuredIds)
    {
        var errors = new List<string>();
        if (!IdPattern().IsMatch(InstrumentId))
            errors.Add("Analyzer ID must be 1 to 40 lower-case letters, digits, '.', '_' or '-' (for example sysmex-xn-1)");
        if (configuredIds.Contains(InstrumentId, StringComparer.Ordinal))
            errors.Add($"'{InstrumentId}' is set up in the service configuration and cannot be registered here");
        if (string.IsNullOrWhiteSpace(Model)) errors.Add("Model is required");
        if (Protocol is not ("astm" or "hl7")) errors.Add("Protocol must be ASTM or HL7");
        if (UtcOffset < TimeSpan.FromHours(-14) || UtcOffset > TimeSpan.FromHours(14)) errors.Add("Clock offset must be between -14:00 and +14:00");

        var t = Transport;
        switch (t.Mode)
        {
            case TransportMode.TcpClient:
                if (string.IsNullOrWhiteSpace(t.Host)) errors.Add("Host is required for a TCP client link");
                if (t.Port is < 1 or > 65535) errors.Add("Port must be between 1 and 65535");
                break;
            case TransportMode.TcpServer:
                if (!IPAddress.TryParse(t.Host, out _)) errors.Add("A TCP server link listens on an IP address (0.0.0.0 for all)");
                if (t.Port is < 1 or > 65535) errors.Add("Port must be between 1 and 65535");
                foreach (var o in others.Where(o => o.Id != InstrumentId && o.Transport.Mode == TransportMode.TcpServer && o.Transport.Port == t.Port))
                    errors.Add($"Port {t.Port} is already used by {o.Id}");
                break;
            case TransportMode.Serial:
                if (string.IsNullOrWhiteSpace(t.PortName)) errors.Add("Serial port name is required (for example COM3)");
                if (t.BaudRate is < 300 or > 921600) errors.Add("Baud rate must be between 300 and 921600");
                if (t.DataBits is < 5 or > 8) errors.Add("Data bits must be 5 to 8");
                if (!Enum.TryParse<Parity>(t.Parity, true, out _)) errors.Add("Parity must be None, Odd, Even, Mark or Space");
                if (!Enum.TryParse<StopBits>(t.StopBits, true, out var sb) || sb == StopBits.None) errors.Add("Stop bits must be One, OnePointFive or Two");
                if (!Enum.TryParse<Handshake>(t.Handshake, true, out _)) errors.Add("Handshake must be None, RequestToSend, XOnXOff or RequestToSendXOnXOff");
                foreach (var o in others.Where(o => o.Id != InstrumentId && o.Transport.Mode == TransportMode.Serial
                                                    && string.Equals(o.Transport.PortName, t.PortName, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"Serial port {t.PortName} is already used by {o.Id}");
                break;
        }

        if (!IsHl7)
        {
            if (Link.ReceiveTimeout <= TimeSpan.Zero || Link.AckTimeout <= TimeSpan.Zero) errors.Add("ASTM timeouts must be positive");
            if (Link.MaxSendAttempts is < 1 or > 20) errors.Add("ASTM send attempts must be 1 to 20");
        }
        errors.AddRange(Profile.Validate(IsHl7));
        return errors;
    }
}
