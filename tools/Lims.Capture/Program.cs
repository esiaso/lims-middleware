using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Lims.Capture;
using Lims.Instruments;
using Lims.Instruments.Astm;
using Lims.Instruments.Hl7;
using Microsoft.Extensions.Logging.Abstractions;

// Site-visit capture tool (design 11.3, Gate 2). It records analyzer traffic without filing or answering anything,
// rebuilds messages, removes patient identity, and turns the result into parser regression tests.
const string Usage = """
    lims-capture listen    --protocol astm|hl7 --out DIR (--tcp-listen ADDR:PORT | --tcp-connect HOST:PORT | --serial PORT
                           [--baud 9600] [--parity None] [--databits 8] [--stopbits One] [--handshake None])
        Acknowledge-only: answers the link layer (ASTM ACK, HL7 ACK^R01 AA) so the analyzer sends, saves each message, and
        never answers a query or sends an order. Writes DIR/capture.jsonl (every byte) and DIR/messages/*.msg.
    lims-capture proxy     --listen ADDR:PORT --upstream HOST:PORT --out DIR
        Passive: sits between the analyzer and an existing host system and records both directions unchanged.
    lims-capture extract   DIR --protocol astm|hl7
        Rebuilds messages from DIR/capture.jsonl into DIR/extracted (rx = from the analyzer, tx = to it). For proxy captures.
    lims-capture anonymize SRC DEST --protocol astm|hl7
        Copies the .msg files with patient identity removed and sample IDs replaced consistently. Check the output by eye.
    lims-capture snapshot  DIR [--parser PARSER-ID | --definition FILE]
        Writes NNNN.expected.json beside each .msg: what the parser makes of it today. Review before committing.
        For an analyzer registered on the Analyzer setup screen, give its definition JSON (GET /api/analyzers); with
        neither option, DIR/definition.json is used if present, else the parser named by the folder.
    Ctrl+C stops listen and proxy.
    """;

if (args.Length == 0) { Console.WriteLine(Usage); return 1; }
string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
string Req(string name) => Opt(name) ?? throw new ArgumentException($"{name} is required");
(string Host, int Port) HostPort(string s) { var i = s.LastIndexOf(':'); return (s[..i], int.Parse(s[(i + 1)..], CultureInfo.InvariantCulture)); }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });

try
{
    switch (args[0])
    {
        case "listen":
        {
            var protocol = Req("--protocol");
            var outDir = Req("--out");
            var transport = new TransportOptions();
            if (Opt("--tcp-listen") is { } l) { (transport.Host, transport.Port) = HostPort(l); transport.Mode = TransportMode.TcpServer; }
            else if (Opt("--tcp-connect") is { } c) { (transport.Host, transport.Port) = HostPort(c); transport.Mode = TransportMode.TcpClient; }
            else if (Opt("--serial") is { } sp)
            {
                transport.Mode = TransportMode.Serial; transport.PortName = sp;
                transport.BaudRate = int.Parse(Opt("--baud") ?? "9600", CultureInfo.InvariantCulture);
                transport.Parity = Opt("--parity") ?? "None"; transport.DataBits = int.Parse(Opt("--databits") ?? "8", CultureInfo.InvariantCulture);
                transport.StopBits = Opt("--stopbits") ?? "One"; transport.Handshake = Opt("--handshake") ?? "None";
            }
            else throw new ArgumentException("give --tcp-listen, --tcp-connect or --serial");

            using var log = new CaptureLog(Path.Combine(outDir, "capture.jsonl"));
            var journal = new MessageFileJournal(Path.Combine(outDir, "messages"));
            Console.WriteLine($"acknowledge-only capture ({protocol}) on {transport.Mode}; writing to {Path.GetFullPath(outDir)}");
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var (stream, owner) = await LinkTransports.OpenAsync(transport, cts.Token);
                    using (owner)
                    await using (var tap = new TapStream(stream, log, "rx", "tx"))
                    {
                        Console.WriteLine($"{DateTime.Now:T} link open");
                        var health = new LinkHealth { InstrumentId = "capture" };
                        if (protocol == "hl7")
                            await new Hl7LinkSession("capture", journal, null, health, TimeProvider.System, NullLogger.Instance).RunAsync(tap, cts.Token);
                        else
                            await new AstmLinkSession("capture", journal, null, new AstmLinkOptions(), health, NullLogger.Instance).RunAsync(tap, cts.Token);
                    }
                    Console.WriteLine($"{DateTime.Now:T} link closed");
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Console.WriteLine($"{DateTime.Now:T} {ex.Message}; retrying in 5 s");
                    try { await Task.Delay(5000, cts.Token); } catch (OperationCanceledException) { break; }
                }
            }
            return 0;
        }
        case "proxy":
        {
            var (lh, lp) = HostPort(Req("--listen"));
            var (uh, up) = HostPort(Req("--upstream"));
            var outDir = Req("--out");
            using var log = new CaptureLog(Path.Combine(outDir, "capture.jsonl"));
            var listener = new TcpListener(IPAddress.Parse(lh), lp);
            listener.Start();
            Console.WriteLine($"passive proxy {lh}:{lp} -> {uh}:{up}; writing to {Path.GetFullPath(outDir)}");
            while (!cts.IsCancellationRequested)
            {
                TcpClient analyzer;
                try { analyzer = await listener.AcceptTcpClientAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                using (analyzer)
                using (var host = new TcpClient())
                {
                    try { await host.ConnectAsync(uh, up, cts.Token); }
                    catch (SocketException ex) { Console.WriteLine($"upstream: {ex.Message}"); continue; }
                    Console.WriteLine($"{DateTime.Now:T} analyzer connected, upstream connected");
                    using var pair = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    async Task Pump(Stream from, Stream to, string dir)
                    {
                        var buf = new byte[8192];
                        try
                        {
                            int n;
                            while ((n = await from.ReadAsync(buf, pair.Token)) > 0)
                            {
                                log.Write(dir, buf.AsSpan(0, n));
                                await to.WriteAsync(buf.AsMemory(0, n), pair.Token);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
                        finally { await pair.CancelAsync(); }
                    }
                    await Task.WhenAll(Pump(analyzer.GetStream(), host.GetStream(), "rx"), Pump(host.GetStream(), analyzer.GetStream(), "tx"));
                    Console.WriteLine($"{DateTime.Now:T} connection closed");
                }
            }
            listener.Stop();
            return 0;
        }
        case "extract":
        {
            var dir = args[1];
            var protocol = Req("--protocol");
            var entries = CaptureLog.Read(Path.Combine(dir, "capture.jsonl")).ToList();
            var outDir = Path.Combine(dir, "extracted"); // apart from listen mode's own messages folder
            var index = MessageFiles.NextIndex(outDir);
            foreach (var d in new[] { "rx", "tx" })
            {
                var bytes = CaptureLog.Bytes(entries, d);
                var messages = protocol == "hl7" ? await Extract.Hl7Async(bytes) : Extract.Astm(bytes);
                foreach (var m in messages) MessageFiles.Save(outDir, ++index, d, m);
                Console.WriteLine($"{d}: {messages.Count} messages");
            }
            return 0;
        }
        case "anonymize":
        {
            var (src, dest) = (args[1], args[2]);
            var protocol = Req("--protocol");
            var anon = new Anonymizer();
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.EnumerateFiles(src, "*.msg").Order())
                await File.WriteAllBytesAsync(Path.Combine(dest, Path.GetFileName(file)), anon.Apply(protocol, await File.ReadAllBytesAsync(file)));
            Console.WriteLine($"{Directory.EnumerateFiles(dest, "*.msg").Count()} files written; {anon.SampleMap.Count} sample IDs replaced.");
            Console.WriteLine("Read every file before it leaves the site: free-text fields this tool does not know about are copied as they are.");
            return 0;
        }
        case "snapshot":
        {
            var dir = args[1];
            var parser = Opt("--parser") is { } id ? Replay.Parser(id)
                : Opt("--definition") is { } def ? Replay.Parser(Replay.LoadDefinition(def))
                : Replay.ForFolder(dir);
            foreach (var file in Directory.EnumerateFiles(dir, "*.msg").Order())
            {
                var json = Replay.Snapshot(parser, await File.ReadAllBytesAsync(file));
                await File.WriteAllTextAsync(Path.ChangeExtension(file, ".expected.json"), json + "\n");
                Console.WriteLine($"{Path.GetFileName(file)}: {(json.Contains("\"success\": true", StringComparison.Ordinal) ? "parsed" : "FAILED")}");
            }
            return 0;
        }
        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Usage);
    return 1;
}
