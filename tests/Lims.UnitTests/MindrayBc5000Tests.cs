using System.Net;
using System.Net.Sockets;
using System.Text;
using Lims.Contracts;
using Lims.Instruments.Astm;
using Lims.Instruments.Hl7;
using Lims.Sim.Bc5000;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lims.UnitTests;

public class Hl7MessageTests
{
    [Fact]
    public void Msh_fields_are_counted_from_the_separator()
    {
        var m = Hl7Message.TryParse("MSH|^~\\&|BC-5000|Mindray|||20261103||ORU^R01|42|P|2.3.1\rPID|1||X^^^^MR\r", out var err)!;
        Assert.Null(err);
        Assert.Equal("ORU", m.MessageType);
        Assert.Equal("R01", m.TriggerEvent);
        Assert.Equal("42", m.ControlId);
        Assert.Equal("P", m.ProcessingId);
        Assert.Equal("2.3.1", m.Version);
        Assert.Equal("X", m.First("PID")!.Component(3, 0));
    }

    [Fact]
    public void Escapes_round_trip()
    {
        var raw = "a|b^c~d\\e&f";
        var esc = Hl7Segment.Escape(raw, Hl7Delimiters.Default);
        Assert.DoesNotContain('|', esc);
        var seg = new Hl7Segment($"NTE|1||{esc}", Hl7Delimiters.Default);
        Assert.Equal(raw, seg.Text(3));
    }

    [Fact]
    public void Missing_msh_is_rejected()
    {
        Assert.Null(Hl7Message.TryParse("PID|1\r", out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public async Task Mllp_skips_noise_and_reads_consecutive_messages()
    {
        var a = Mllp.Frame(Encoding.UTF8.GetBytes("MSH|one"));
        var b = Mllp.Frame(Encoding.UTF8.GetBytes("MSH|two"));
        using var s = new MemoryStream([0x41, 0x0D, .. a, .. b]);
        Assert.Equal("MSH|one", Encoding.UTF8.GetString((await Mllp.ReadAsync(s, default))!));
        Assert.Equal("MSH|two", Encoding.UTF8.GetString((await Mllp.ReadAsync(s, default))!));
        Assert.Null(await Mllp.ReadAsync(s, default));
    }

    [Fact]
    public async Task Mllp_stream_ending_inside_a_message_throws()
    {
        using var s = new MemoryStream([Mllp.StartBlock, .. "MSH|"u8.ToArray()]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Mllp.ReadAsync(s, default));
    }
}

public class MindrayBc5000ParserTests
{
    private static readonly MindrayBc5000Parser Parser = new(TimeSpan.FromHours(3));
    private static readonly DateTime At = new(2026, 11, 3, 9, 30, 0);

    private static ParseOutcome Parse(string message) => Parser.Parse("bc5000-1", Encoding.UTF8.GetBytes(message), "corr-1");

    [Fact]
    public void Patient_result_keeps_parameters_and_drops_run_information()
    {
        var sim = new Bc5000Simulator(Stream.Null);
        var r = Parse(sim.ResultMessage("261103000101", Bc5000Simulator.NormalCbc(), At));
        Assert.True(r.Success, r.Error);
        var m = Assert.Single(r.Messages);
        Assert.Equal(MessageKind.PatientResult, m.Kind);
        Assert.Equal("261103000101", m.SampleId);
        Assert.Equal(Bc5000Simulator.NormalCbc().Select(p => p.Code), m.Results.Select(x => x.InstrumentTestCode));
        var wbc = m.Results[0];
        Assert.Equal(6.45m, wbc.NumericValue);
        Assert.Equal("10*9/L", wbc.Units);
        Assert.Equal("4.00-10.00", wbc.ReferenceRangeText);
        Assert.Empty(wbc.InstrumentFlags); // "N" is normal, not a flag
        Assert.Equal("F", wbc.ResultStatus);
        Assert.Equal(new DateTimeOffset(At, TimeSpan.FromHours(3)), wbc.RunAt);
    }

    [Fact]
    public void Abnormal_flags_and_alerts_become_flags()
    {
        var sim = new Bc5000Simulator(Stream.Null);
        var p = Bc5000Simulator.NormalCbc().Select(x => x.Code == "6690-2" ? x with { Value = "15.20", Flag = "H~A" } : x).ToList();
        var r = Parse(sim.ResultMessage("S2", p, At, alerts: ["12004^Lymphocytosis?"]));
        var res = Assert.Single(r.Messages).Results;
        Assert.Equal(new[] { "H", "A", "alert:Lymphocytosis?" }, res[0].InstrumentFlags);
        Assert.Contains("alert:Lymphocytosis?", res[^1].InstrumentFlags);
        Assert.DoesNotContain(res, x => x.InstrumentTestCode is "08003" or "30525-0" or "15000" or "12004");
    }

    [Fact]
    public void Qc_message_is_qc_with_lot()
    {
        var sim = new Bc5000Simulator(Stream.Null);
        var r = Parse(sim.QcMessage("01", "LOT2611", Bc5000Simulator.NormalCbc().Take(3), At));
        var m = Assert.Single(r.Messages);
        Assert.Equal(MessageKind.Qc, m.Kind);
        Assert.Equal("01", m.ControlName);
        Assert.Equal("LOT2611", m.ControlLot);
        Assert.Equal(3, m.Results.Count); // 05001 Qc Level is not a result
    }

    [Fact]
    public void Research_use_and_edit_flags()
    {
        var msg = "MSH|^~\\&|||||20261103093000||ORU^R01|9|P|2.3.1\r" +
                  "OBR|1||S3|00001^Automated Count^99MRC\r" +
                  "OBX|1|NM|10000^*ALY#^99MRC||0.02|10*9/L|||||F|||20261103093000\r" +
                  "OBX|2|NM|718-7^HGB^LN||140|g/L|110-160|N|||C||E|20261103093000\r";
        var res = Assert.Single(Parse(msg).Messages).Results;
        Assert.Contains("RUO", res[0].InstrumentFlags);
        Assert.Contains("edited on instrument", res[1].InstrumentFlags);
        Assert.True(res[1].IsRerun);
    }

    [Fact]
    public void Worklist_query_carries_sample_id()
    {
        var sim = new Bc5000Simulator(Stream.Null);
        var m = Assert.Single(Parse(sim.WorklistQuery("S4", At)).Messages);
        Assert.Equal(MessageKind.Query, m.Kind);
        Assert.Equal("S4", m.SampleId);
        Assert.Equal("1", m.Query!.SequenceNumber); // MSH-10, echoed in the reply's MSA-2
    }

    [Fact]
    public void Unsupported_and_invalid_messages_fail()
    {
        Assert.False(Parse("MSH|^~\\&|||||20261103||ADT^A01|1|P|2.3.1\r").Success);
        Assert.False(Parse("not hl7").Success);
        Assert.False(Parser.Parse("bc5000-1", [0x4D, 0x53, 0x48, 0xFF, 0xFE], "c").Success);
    }

    [Fact]
    public void Ack_and_worklist_reply_shapes()
    {
        var now = new DateTimeOffset(2026, 11, 3, 9, 30, 0, TimeSpan.Zero);
        var ack = MindrayBc5000Replies.Ack("AE", "17", "Q", now, 207, "Application internal error");
        var segs = ack.Split('\r');
        Assert.Contains("ACK^R01", segs[0]);
        Assert.Equal("Q", segs[0].Split('|')[10]);
        Assert.Equal("MSA|AE|17|Application internal error||||207", segs[1]);

        var cbc = MindrayBc5000Replies.WorklistReply("5", "S5", new InstrumentOrder("S5", "routine", ["6690-2", "718-7"]), now);
        Assert.Contains("ORR^O02", cbc);
        Assert.Contains("MSA|AA|5", cbc);
        Assert.Contains("ORC|AF|S5", cbc);
        Assert.Contains("08003^Test Mode^99MRC||CBC|", cbc);

        var diff = MindrayBc5000Replies.WorklistReply("5", "S5", new InstrumentOrder("S5", "routine", ["6690-2", "770-8"]), now);
        Assert.Contains("||CBC+DIFF|", diff);

        var none = MindrayBc5000Replies.WorklistReply("5", "S5", null, now);
        Assert.Equal(2, none.Split('\r', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}

public sealed class Hl7LinkSessionTests : IAsyncLifetime
{
    private TcpListener _listener = null!;
    private TcpClient _instrumentSide = null!;
    private TcpClient _hostSide = null!;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private readonly FakeJournal _journal = new();
    private readonly LinkHealth _health = new() { InstrumentId = "bc5000-1" };
    private Task _session = Task.CompletedTask;
    private Bc5000Simulator _sim = null!;
    private static readonly DateTime At = new(2026, 11, 3, 9, 30, 0);

    public async Task InitializeAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _instrumentSide = new TcpClient();
        var accept = _listener.AcceptTcpClientAsync();
        await _instrumentSide.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        _hostSide = await accept;
        _sim = new Bc5000Simulator(_instrumentSide.GetStream(), TimeSpan.FromSeconds(5));
    }

    private void StartSession(string? reply = null) =>
        _session = Task.Run(() => new Hl7LinkSession("bc5000-1", _journal, new FixedResponder(reply), _health, TimeProvider.System, NullLogger.Instance)
            .RunAsync(_hostSide.GetStream(), _cts.Token));

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try { await _session; } catch { /* cancelled */ }
        _instrumentSide.Dispose(); _hostSide.Dispose(); _listener.Stop();
    }

    [Fact]
    public async Task Result_is_committed_then_acknowledged_AA()
    {
        StartSession();
        var msg = _sim.ResultMessage("S1", Bc5000Simulator.NormalCbc(), At);
        var reply = await _sim.SendAsync(msg);
        Assert.Equal("AA", Bc5000Simulator.AckCode(reply));
        Assert.Contains("MSA|AA|1", reply);
        Assert.Equal(msg, Assert.Single(_journal.Messages));
        Assert.Equal(1, _health.MessagesToday);
    }

    [Fact]
    public async Task Storage_unavailable_is_answered_AE_207_and_not_stored()
    {
        _journal.Available = false;
        StartSession();
        var reply = await _sim.SendAsync(_sim.ResultMessage("S1", Bc5000Simulator.NormalCbc(), At));
        Assert.Equal("AE", Bc5000Simulator.AckCode(reply));
        Assert.EndsWith("|207", reply!.Split('\r')[1]);
        Assert.Empty(_journal.Messages);
    }

    [Fact]
    public async Task Commit_failure_is_AE_and_the_resend_is_stored_once()
    {
        _journal.FailCommits = 1;
        StartSession();
        var msg = _sim.ResultMessage("S1", Bc5000Simulator.NormalCbc(), At);
        Assert.Equal("AE", Bc5000Simulator.AckCode(await _sim.SendAsync(msg)));
        Assert.Equal("AA", Bc5000Simulator.AckCode(await _sim.SendAsync(msg)));
        Assert.Single(_journal.Messages);
    }

    [Fact]
    public async Task Worklist_query_gets_the_responder_reply()
    {
        var orr = MindrayBc5000Replies.WorklistReply("1", "S7", new InstrumentOrder("S7", "routine", ["6690-2"]), DateTimeOffset.UtcNow);
        StartSession(orr);
        var reply = await _sim.SendAsync(_sim.WorklistQuery("S7", At));
        Assert.Equal(orr, reply);
        Assert.Single(_journal.Messages);
        Assert.Equal("query.reply", Assert.Single(_journal.Outbound).Kind);
    }

    [Fact]
    public async Task Worklist_query_without_order_gets_msa_only()
    {
        StartSession();
        var reply = await _sim.SendAsync(_sim.WorklistQuery("S8", At));
        Assert.Contains("ORR^O02", reply);
        Assert.DoesNotContain("ORC|", reply);
    }

    [Fact]
    public async Task Garbage_is_stored_for_review_and_answered_AE()
    {
        StartSession();
        var reply = await _sim.SendAsync("this is not hl7");
        Assert.Equal("AE", Bc5000Simulator.AckCode(reply));
        Assert.Single(_journal.Messages); // kept in the inbox, quarantined by the parser
    }

    [Fact]
    public async Task Several_messages_on_one_connection()
    {
        StartSession();
        for (var i = 0; i < 5; i++)
            Assert.Equal("AA", Bc5000Simulator.AckCode(await _sim.SendAsync(_sim.ResultMessage($"S{i}", Bc5000Simulator.NormalCbc(), At))));
        Assert.Equal(5, _journal.Messages.Count);
    }
}
