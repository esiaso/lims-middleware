using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace Lims.Instruments;

/// <summary>Opens the byte stream for an analyzer link. The same ASTM code runs above every transport.</summary>
public static class LinkTransports
{
    public static async Task<(Stream Stream, IDisposable? Owner)> OpenAsync(TransportOptions t, CancellationToken ct)
    {
        switch (t.Mode)
        {
            case TransportMode.TcpClient:
            {
                var client = new TcpClient { NoDelay = true };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                await client.ConnectAsync(t.Host, t.Port, cts.Token);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                return (client.GetStream(), client);
            }
            case TransportMode.TcpServer:
            {
                var listener = new TcpListener(IPAddress.Parse(t.Host), t.Port);
                listener.Start(1);
                try
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    client.NoDelay = true;
                    return (client.GetStream(), client);
                }
                finally { listener.Stop(); }
            }
            case TransportMode.Serial:
            {
                var port = new SerialPort(t.PortName ?? throw new InvalidOperationException("Serial transport needs PortName"),
                    t.BaudRate, Enum.Parse<Parity>(t.Parity, true), t.DataBits, Enum.Parse<StopBits>(t.StopBits, true))
                {
                    Handshake = Enum.Parse<Handshake>(t.Handshake, true),
                };
                port.Open();
                return (port.BaseStream, port);
            }
            default:
                throw new NotSupportedException(t.Mode.ToString());
        }
    }
}
