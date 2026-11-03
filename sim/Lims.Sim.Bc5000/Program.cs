using System.Net;
using System.Net.Sockets;
using Lims.Sim.Bc5000;

// Mindray BC-5000 simulator. Like the analyzer, it connects to the middleware's LIS port (Transport.Mode = TcpServer).
// The connection direction is still to be confirmed on site (design 4.1). Synthetic data only.
//
//   dotnet run --project sim/Lims.Sim.Bc5000 -- --port 5100 result 261102000427
//   dotnet run --project sim/Lims.Sim.Bc5000 -- --port 5100 query 261102000427
var port = 5100;
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

using var client = new TcpClient();
await client.ConnectAsync(IPAddress.Loopback, port);
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
