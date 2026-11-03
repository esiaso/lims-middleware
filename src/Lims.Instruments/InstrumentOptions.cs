using Lims.Instruments.Astm;
using Lims.Instruments.Generic;

namespace Lims.Instruments;

public enum TransportMode { TcpClient, TcpServer, Serial }

public sealed class TransportOptions
{
    public TransportMode Mode { get; set; } = TransportMode.TcpClient;
    /// <summary>TcpClient: serial device server or instrument address. TcpServer: local bind address.</summary>
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string? PortName { get; set; }
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";
    /// <summary>None, RequestToSend (RTS/CTS) or XOnXOff.</summary>
    public string Handshake { get; set; } = "None";
}

public sealed class InstrumentConfig
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "cobas e 411";
    public string Parser { get; set; } = CobasE411Parser.Id;
    public E411ProtocolVariant Variant { get; set; } = E411ProtocolVariant.Cobas;
    public bool Enabled { get; set; } = true;
    /// <summary>Answer TSREQ^REAL host queries. Off means results-only with load lists (design 4).</summary>
    public bool HostQuery { get; set; } = true;
    public string? SoftwareVersion { get; set; }
    /// <summary>Instrument clock offset from UTC. Kenya (EAT) is +03:00 with no daylight saving.</summary>
    public TimeSpan UtcOffset { get; set; } = TimeSpan.FromHours(3);
    public TransportOptions Transport { get; set; } = new();
    public AstmLinkOptions Link { get; set; } = new();
    /// <summary>Message layout for the generic adapters (generic.astm, generic.hl7); unused by the built-in ones.</summary>
    public GenericAdapterProfile Generic { get; set; } = new();
    /// <summary>The approved analyzer definition this entry came from; null for analyzers listed in appsettings.</summary>
    public Guid? DefinitionId { get; set; }
    public int? DefinitionVersion { get; set; }
    public bool IsRegistered => DefinitionId is not null;
}

public sealed class InstrumentsOptions
{
    public List<InstrumentConfig> Instruments { get; set; } = new();
    /// <summary>How often the link worker looks for analyzers registered, changed or disabled from the screens.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
}
