using System.Net;
using System.Net.Sockets;
using Lims.Sim.E411;

// cobas e 411 simulator. It plays the serial device server: it listens on a TCP port and the middleware
// connects to it (Transport.Mode = TcpClient). Synthetic data only.
//
//   dotnet run --project sim/Lims.Sim.E411 -- --port 4001 result 261102000427 10=1.25:uIU/mL 30=0.091:ng/dL
//   dotnet run --project sim/Lims.Sim.E411 -- --port 4001 query 261102000427
//   dotnet run --project sim/Lims.Sim.E411 -- --port 4001 qc PCU1 123456 10=1.45:uIU/mL
var port = 4001;
var rest = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--port") port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else rest.Add(args[i]);
}
if (rest.Count < 2)
{
    Console.WriteLine("usage: [--port N] result <sampleId> <code=value:unit[:flag]>... | query <sampleId> | qc <control> <lot> <code=value:unit>");
    return 1;
}

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.WriteLine($"e411 simulator listening on 127.0.0.1:{port}; waiting for the middleware to connect...");
using var client = await listener.AcceptTcpClientAsync();
var sim = new E411Simulator(client.GetStream());
Console.WriteLine("connected");

static SimResult ParseResult(string s)
{
    var (code, rest) = (s.Split('=')[0], s.Split('=', 2)[1]);
    var p = rest.Split(':');
    return new SimResult(code, p[0], p.Length > 1 ? p[1] : "", p.Length > 2 ? p[2] : "N");
}

switch (rest[0])
{
    case "result":
        var status = await sim.SendAsync(E411Simulator.ResultMessage(rest[1], rest.Skip(2).Select(ParseResult), DateTime.Now));
        Console.WriteLine($"result upload: {status}");
        break;
    case "qc":
        var r = ParseResult(rest[3]);
        Console.WriteLine($"qc upload: {await sim.SendAsync(E411Simulator.QcMessage(rest[1], rest[2], r.TestCode, r.Value, r.Units, DateTime.Now))}");
        break;
    case "query":
        Console.WriteLine($"query: {await sim.SendAsync(E411Simulator.QueryMessage(rest[1]))}");
        var reply = await sim.ReceiveAsync();
        Console.WriteLine("host reply:");
        Console.WriteLine(reply?.Replace("\r", Environment.NewLine) ?? "(none within 15 s)");
        break;
    default:
        Console.WriteLine($"unknown command {rest[0]}");
        return 1;
}
return 0;
