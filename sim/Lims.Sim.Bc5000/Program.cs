using System.Net;
using System.Net.Sockets;
using Lims.Sim.Bc5000;

// Mindray BC-5000 simulator. It listens on a TCP port and the middleware connects to it (Transport.Mode = TcpClient).
// The real instrument's TCP role is still to be confirmed on site (design 4.1). Synthetic data only.
//
//   dotnet run --project sim/Lims.Sim.Bc5000 -- --port 5101 result 261102000427
//   dotnet run --project sim/Lims.Sim.Bc5000 -- --port 5101 query 261102000427
var port = 5101;
var rest = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--port") port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else rest.Add(args[i]);
}
if (rest.Count < 2)
{
    Console.WriteLine("usage: [--port N] result <sampleId> | query <sampleId> | qc <fileNo> <lot>");
    return 1;
}

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.WriteLine($"BC-5000 simulator listening on 127.0.0.1:{port}; waiting for the middleware to connect...");
using var client = await listener.AcceptTcpClientAsync();
var sim = new Bc5000Simulator(client.GetStream());
Console.WriteLine("connected");

var reply = rest[0] switch
{
    "result" => await sim.SendAsync(sim.ResultMessage(rest[1], Bc5000Simulator.NormalCbc(), DateTime.Now)),
    "query" => await sim.SendAsync(sim.WorklistQuery(rest[1], DateTime.Now)),
    "qc" => await sim.SendAsync(sim.QcMessage(rest[1], rest.ElementAtOrDefault(2) ?? "LOT1", Bc5000Simulator.NormalCbc().Take(3), DateTime.Now)),
    _ => null,
};
Console.WriteLine(reply?.Replace("\r", Environment.NewLine) ?? "(no reply)");
return 0;
