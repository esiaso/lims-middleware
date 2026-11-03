using System.Net;
using System.Net.Sockets;
using System.Text;
using Lims.Capture;
using Lims.Contracts;
using Lims.Instruments.Astm;
using Lims.Instruments.Hl7;
using Lims.Sim.Bc5000;
using Lims.Sim.E411;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lims.UnitTests;

public sealed class CaptureToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lims-capture-{Guid.NewGuid():N}");
    private static readonly DateTime At = new(2026, 11, 3, 9, 30, 0);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (DirectoryNotFoundException) { } }

    private static byte[] AstmWire(string message, bool corruptFirst = false, bool duplicateFirst = false)
    {
        var bytes = new List<byte> { AstmControl.ENQ };
        var frames = E411Simulator.SplitFrames(message);
        var fn = 1;
        for (var i = 0; i < frames.Count; i++)
        {
            if (i == 0 && corruptFirst) bytes.AddRange(E411Simulator.Frame(fn, frames[i].Text, frames[i].Final, corrupt: true));
            bytes.AddRange(E411Simulator.Frame(fn, frames[i].Text, frames[i].Final));
            if (i == 0 && duplicateFirst) bytes.AddRange(E411Simulator.Frame(fn, frames[i].Text, frames[i].Final));
            fn = (fn + 1) % 8;
        }
        bytes.Add(AstmControl.EOT);
        return bytes.ToArray();
    }

    [Fact]
    public void Astm_extract_rebuilds_messages_and_skips_bad_and_repeated_frames()
    {
        var a = E411Simulator.ResultMessage("261103000101", [new SimResult("10", "1.25", "mIU/L")], At);
        var b = C111Messages.Results("261103000102", [new SimResult("989", "5.1", "mmol/L")], At);
        var wire = AstmWire(a, corruptFirst: true).Concat(AstmWire(b, duplicateFirst: true)).Concat(AstmWire(a)[..20]).ToArray();
        var messages = Extract.Astm(wire);
        Assert.Equal([a, b], messages.Select(m => Encoding.ASCII.GetString(m)));
    }

    [Fact]
    public async Task Hl7_extract_rebuilds_messages_and_ignores_a_cut_off_tail()
    {
        var sim = new Bc5000Simulator(Stream.Null);
        var m1 = sim.ResultMessage("S1", Bc5000Simulator.NormalCbc(), At);
        var m2 = sim.WorklistQuery("S2", At);
        var wire = Mllp.Frame(Encoding.UTF8.GetBytes(m1)).Concat(Mllp.Frame(Encoding.UTF8.GetBytes(m2))).Concat(new byte[] { 0x0B, 0x4D }).ToArray();
        var messages = await Extract.Hl7Async(wire);
        Assert.Equal([m1, m2], messages.Select(m => Encoding.UTF8.GetString(m)));
    }

    [Fact]
    public void Astm_anonymizer_keeps_results_and_replaces_ids_consistently()
    {
        var anon = new Anonymizer();
        var query = anon.Astm(E411Simulator.QueryMessage("261103000101"));
        var result = anon.Astm("H|\\^&|||cobas-e411^1|||||host|RSUPL^REAL|P|1\rP|1||PID123|PID456|Doe^Jane||19800101|F\r" +
            "O|1|261103000101|40^0^5^^S1^SC|^^^10^|R||||||N||||1|Dr Smith|0722000000|||||20261103093000|||F\r" +
            "R|1|^^^10//not|1.25^|mIU/L||N||F||jsmith|||E1\rL|1|N\r");
        var c111 = anon.Astm(C111Messages.Results("261103000101", [new SimResult("989", "5.1", "mmol/L")], At));
        Assert.Contains("^^ANON000001^40", query);
        Assert.DoesNotContain("261103000101", result + c111 + query);
        Assert.DoesNotContain("Doe", result);
        Assert.DoesNotContain("19800101", result);
        Assert.DoesNotContain("Dr Smith", result);
        Assert.DoesNotContain("jsmith", result);

        var parsed = new CobasE411Parser(TimeSpan.FromHours(3)).Parse("e", Encoding.ASCII.GetBytes(result), "c");
        var m = Assert.Single(parsed.Messages);
        Assert.Equal("ANON000001", m.SampleId);
        Assert.Equal(1.25m, Assert.Single(m.Results).NumericValue);
        Assert.Equal("ANON000001", Assert.Single(new CobasC111Parser(TimeSpan.FromHours(3)).Parse("c", Encoding.ASCII.GetBytes(c111), "c").Messages).SampleId);
    }

    [Fact]
    public void Hl7_anonymizer_removes_identity_and_keeps_qc_lot()
    {
        var sim = new Bc5000Simulator(Stream.Null);
        var anon = new Anonymizer();
        var patient = anon.Hl7(sim.ResultMessage("261103000101", Bc5000Simulator.NormalCbc(), At, patientId: "MRN-778899"));
        Assert.DoesNotContain("MRN-778899", patient);
        Assert.DoesNotContain("Synthetic", patient);
        Assert.DoesNotContain("261103000101", patient);
        Assert.DoesNotContain("tech1", patient);
        Assert.Contains("19900101", patient); // year kept, day and month set to 01 01
        var parsed = new MindrayBc5000Parser(TimeSpan.FromHours(3)).Parse("b", Encoding.UTF8.GetBytes(patient), "c");
        var m = Assert.Single(parsed.Messages);
        Assert.Equal("ANON000001", m.SampleId);
        Assert.Equal(Bc5000Simulator.NormalCbc().Count, m.Results.Count);

        var qc = anon.Hl7(sim.QcMessage("01", "LOT2611", Bc5000Simulator.NormalCbc().Take(2), At));
        Assert.Equal("LOT2611", Assert.Single(new MindrayBc5000Parser(TimeSpan.FromHours(3)).Parse("b", Encoding.UTF8.GetBytes(qc), "c").Messages).ControlLot);
    }

    [Fact]
    public async Task Listen_mode_saves_messages_and_logs_every_byte()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var analyzer = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await analyzer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var host = await accept;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var logPath = Path.Combine(_dir, "capture.jsonl");
        using (var log = new CaptureLog(logPath))
        {
            var journal = new MessageFileJournal(Path.Combine(_dir, "messages"));
            var session = Task.Run(() => new AstmLinkSession("capture", journal, null, new AstmLinkOptions { IdlePoll = TimeSpan.FromMilliseconds(50) },
                new LinkHealth(), NullLogger.Instance).RunAsync(new TapStream(host.GetStream(), log, "rx", "tx"), cts.Token));
            var sim = new E411Simulator(analyzer.GetStream(), TimeSpan.FromSeconds(5));
            var msg = E411Simulator.ResultMessage("261103000101", [new SimResult("10", "1.25", "mIU/L")], At);
            Assert.Equal(SendStatus.Accepted, await sim.SendAsync(msg));
            Assert.Equal(SendStatus.Accepted, await sim.SendAsync(E411Simulator.QueryMessage("261103000101")));
            Assert.Null(await new E411Simulator(analyzer.GetStream(), TimeSpan.FromSeconds(1)).ReceiveAsync()); // no answer to a query
            await cts.CancelAsync();
            try { await session; } catch (OperationCanceledException) { }
            listener.Stop();

            var files = Directory.GetFiles(Path.Combine(_dir, "messages"), "*.msg").Order().ToList();
            Assert.Equal(2, files.Count);
            Assert.Equal(msg, await File.ReadAllTextAsync(files[0]));
        }
        var entries = CaptureLog.Read(logPath).ToList();
        Assert.Equal(2, Extract.Astm(CaptureLog.Bytes(entries, "rx")).Count);
        Assert.Contains(AstmControl.ACK, CaptureLog.Bytes(entries, "tx"));
    }
}

/// <summary>
/// Real traffic captured at the site visit, anonymized, lives in tests/captures/&lt;parser id&gt;/. Each .msg must parse, and
/// match its .expected.json when one is committed. Empty until the visit: the test then has nothing to check.
/// </summary>
public class CaptureReplayTests
{
    public static string CapturesRoot
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "captures"))) dir = Path.GetDirectoryName(dir);
            return dir is null ? "" : Path.Combine(dir, "tests", "captures");
        }
    }

    [Fact]
    public void Every_captured_message_parses_and_matches_its_snapshot()
    {
        Assert.True(Directory.Exists(CapturesRoot), "tests/captures not found");
        var failures = new List<string>();
        foreach (var parserDir in Directory.GetDirectories(CapturesRoot))
        {
            var parser = Path.GetFileName(parserDir);
            var replay = Replay.ForFolder(parserDir);
            foreach (var file in Directory.EnumerateFiles(parserDir, "*.msg", SearchOption.AllDirectories))
            {
                var json = Replay.Snapshot(replay, File.ReadAllBytes(file));
                var expectedPath = Path.ChangeExtension(file, ".expected.json");
                if (!json.Contains("\"success\": true", StringComparison.Ordinal))
                    failures.Add($"{parser}/{Path.GetFileName(file)} does not parse: {json}");
                else if (File.Exists(expectedPath) && File.ReadAllText(expectedPath).TrimEnd() != json.TrimEnd())
                    failures.Add($"{parser}/{Path.GetFileName(file)} differs from its .expected.json");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Capture_folders_are_named_after_known_parsers_or_hold_a_definition()
    {
        foreach (var d in Directory.Exists(CapturesRoot) ? Directory.GetDirectories(CapturesRoot) : [])
            if (!File.Exists(Path.Combine(d, Replay.DefinitionFile)))
                Assert.Contains(Path.GetFileName(d), Lims.Instruments.ParserRegistry.BuiltInParsers);
    }

    [Fact]
    public void A_registered_analyzer_folder_replays_with_its_definition()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}", "chem-astm-1");
        Directory.CreateDirectory(dir);
        try
        {
            var d = new Lims.Instruments.Registration.AnalyzerDefinition { InstrumentId = "chem-astm-1", Model = "Example", Protocol = "astm" };
            d.Profile.AstmTestCode = "R-3.1"; // this analyzer sends the code first, not in component 4
            File.WriteAllText(Path.Combine(dir, Replay.DefinitionFile), d.ToJson());
            var msg = System.Text.Encoding.ASCII.GetBytes("H|\\^&\rO|1|S1||^^^GLU\rR|1|GLU|5.4|mmol/L\rL|1\r");

            var json = Replay.Snapshot(Replay.ForFolder(dir), msg);
            Assert.Contains("\"success\": true", json);
            Assert.Contains("\"instrumentTestCode\": \"GLU\"", json);
            // The default layout reads component 4, which this analyzer leaves empty: nothing is read.
            Assert.DoesNotContain("\"instrumentTestCode\": \"GLU\"", Replay.Snapshot("generic.astm", msg));

            d.Profile.AstmTestCode = "bad";
            File.WriteAllText(Path.Combine(dir, Replay.DefinitionFile), d.ToJson());
            Assert.Throws<ArgumentException>(() => Replay.ForFolder(dir));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }
}
