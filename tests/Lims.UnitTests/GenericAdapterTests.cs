using System.Net;
using System.Net.Sockets;
using System.Text;
using Lims.Contracts;
using Lims.Instruments;
using Lims.Instruments.Astm;
using Lims.Instruments.Generic;
using Lims.Instruments.Hl7;
using Lims.Instruments.Registration;
using Lims.Sim.Bc5000;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lims.UnitTests;

public class FieldRefTests
{
    [Theory]
    [InlineData("R-4", "R", 4, null)]
    [InlineData("r-3.4", "R", 3, 4)]
    [InlineData(" OBX-3.1 ", "OBX", 3, 1)]
    public void Parses_positions(string text, string record, int field, int? component)
    {
        var f = FieldRef.Parse(text);
        Assert.Equal(new FieldRef(record, field, component), f);
    }

    [Theory]
    [InlineData("")]
    [InlineData("R4")]
    [InlineData("R-0")]
    [InlineData("R-3.")]
    [InlineData("R-3.x")]
    [InlineData("OBXX-1")]
    [InlineData("R-1.2.3")]
    public void Rejects_malformed_positions(string text) => Assert.False(FieldRef.TryParse(text, out _, out _));
}

public class GenericAstmParserTests
{
    private static readonly TimeSpan Eat = TimeSpan.FromHours(3);

    private static ParseOutcome Parse(string message, GenericAdapterProfile? p = null) =>
        new GenericAstmParser(p ?? new GenericAdapterProfile(), Eat).Parse("x-1", Encoding.ASCII.GetBytes(message), "c1");

    private const string Results =
        "H|\\^&|||ANALYZER^1.0|||||||P|1|20261103101500\r" +
        "P|1||PID1\r" +
        "O|1|261103000101||^^^CRP\\^^^ALB|R||||||N||||||||||||||F\r" +
        "R|1|^^^CRP|12.5|mg/L|0^5|H||F||op1||20261103101200\r" +
        "C|1|I|61^Sample short|I\r" +
        "R|2|^^^ALB|<10|g/L|35\\52|L||C||op1||20261103101300\r" +
        "R|3|^^^MODE|1|||N||F\r" +
        "L|1|N\r";

    [Fact]
    public void Default_layout_reads_standard_e1394_results()
    {
        var outcome = Parse(Results, new GenericAdapterProfile { IgnoredTestCodes = ["MODE"] });
        Assert.True(outcome.Success, outcome.Error);
        var m = Assert.Single(outcome.Messages);
        Assert.Equal(MessageKind.PatientResult, m.Kind);
        Assert.Equal("261103000101", m.SampleId);
        Assert.Equal(2, m.Results.Count);

        var crp = m.Results[0];
        Assert.Equal("CRP", crp.InstrumentTestCode);
        Assert.Equal(12.5m, crp.NumericValue);
        Assert.Equal("mg/L", crp.Units);
        Assert.Equal("0-5", crp.ReferenceRangeText);
        Assert.Equal(["H", "comment:61"], crp.InstrumentFlags);
        Assert.Equal("op1", crp.OperatorId);
        Assert.False(crp.IsRerun);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 10, 12, 0, Eat), crp.RunAt);

        var alb = m.Results[1];
        Assert.Equal("<", alb.Comparator);
        Assert.Equal(10m, alb.NumericValue);
        Assert.Equal("35-52", alb.ReferenceRangeText);
        Assert.True(alb.IsRerun);
    }

    [Fact]
    public void Custom_positions_are_used()
    {
        // An analyzer that sends the tube ID in O-4 component 1, the code in R-3 component 1 and the value in R-4 component 2.
        var msg = "H|\\^&|||A\rP|1\rO|1||ABC123^4|^^^GLU\rR|1|GLU^Glucose|^5.4|mmol/L\rL|1\r";
        var p = new GenericAdapterProfile { AstmSampleId = "O-4.1", AstmTestCode = "R-3.1", AstmValue = "R-4.2", AstmCompletedAt = null };
        var m = Assert.Single(Parse(msg, p).Messages);
        Assert.Equal("ABC123", m.SampleId);
        var r = Assert.Single(m.Results);
        Assert.Equal("GLU", r.InstrumentTestCode);
        Assert.Equal(5.4m, r.NumericValue);
    }

    [Fact]
    public void Sample_id_falls_back_when_the_main_field_is_empty()
    {
        var msg = "H|\\^&\rO|1||T42^1^2|^^^K\rR|1|^^^K|4.1\rL|1\r";
        Assert.Equal("T42", Assert.Single(Parse(msg).Messages).SampleId);
    }

    [Fact]
    public void Qc_is_detected_by_action_code_or_sample_prefix()
    {
        var byAction = "H|\\^&\rO|1|CTRL-L1||^^^CRP|R||||||Q\rR|1|^^^CRP|4.9|mg/L\rL|1\r";
        var m = Assert.Single(Parse(byAction).Messages);
        Assert.Equal(MessageKind.Qc, m.Kind);
        Assert.Equal("CTRL-L1", m.ControlName);

        var byPrefix = "H|\\^&\rO|1|QC0002||^^^CRP\rR|1|^^^CRP|4.9|mg/L\rL|1\r";
        Assert.Equal(MessageKind.PatientResult, Assert.Single(Parse(byPrefix).Messages).Kind);
        Assert.Equal(MessageKind.Qc, Assert.Single(Parse(byPrefix, new GenericAdapterProfile { QcSampleIdPrefixes = ["QC"] }).Messages).Kind);
    }

    [Fact]
    public void Query_and_cancel_are_read()
    {
        var q = Assert.Single(Parse("H|\\^&\rQ|1|^261103000101||ALL||||||||O\rL|1|N\r").Messages);
        Assert.Equal(MessageKind.Query, q.Kind);
        Assert.Equal("261103000101", q.SampleId);
        Assert.False(q.Query!.IsCancel);
        Assert.True(Assert.Single(Parse("H|\\^&\rQ|1|^X||ALL||||||||A\rL|1\r").Messages).Query!.IsCancel);
    }

    [Fact]
    public void Not_completed_result_has_no_value_and_a_flag()
    {
        var r = Assert.Single(Assert.Single(Parse("H|\\^&\rO|1|S1||^^^K\rR|1|^^^K|99|mmol/L||||X\rL|1\r").Messages).Results);
        Assert.Equal("", r.RawValue);
        Assert.Contains("not completed", r.InstrumentFlags);
    }

    [Fact]
    public void Broken_messages_fail_instead_of_guessing()
    {
        Assert.False(Parse("P|1\rL|1\r").Success);
        Assert.False(Parse("H|\\^&\rR|1|^^^K|1\rL|1\r").Success);
        Assert.False(Parse("H|\\^&\rL|1\r").Success);
    }

    [Fact]
    public void Order_reply_lists_tests_and_an_empty_reply_runs_nothing()
    {
        var p = new GenericAdapterProfile { HostName = "LIS", ReceiverName = "XN", MaxTestsPerOrder = 2 };
        var now = new DateTimeOffset(2026, 11, 3, 10, 0, 0, Eat);
        var reply = GenericAstmMessages.Order(p, "261103000101", new InstrumentOrder("261103000101", "stat", ["CRP", "ALB", "TP", "CRP"]), now);
        Assert.StartsWith("H|\\^&|||LIS|||||XN||P|1|20261103100000\rP|1\r", reply);
        Assert.Contains("O|1|261103000101||^^^CRP\\^^^ALB|S|20261103100000|||||A", reply);
        Assert.EndsWith("|O\rL|1|N\r", reply);
        Assert.Equal("H|\\^&|||LIS|||||XN||P|1|20261103100000\rL|1|N\r", GenericAstmMessages.Order(p, "X", null, now));

        // The generic parser reads its own order message back: same sample, same tests.
        var back = new GenericAstmParser(p, Eat).Parse("x", Encoding.ASCII.GetBytes(reply), "c");
        Assert.Equal("261103000101", Assert.Single(back.Messages).SampleId);
    }
}

public class GenericHl7ParserTests
{
    private static readonly TimeSpan Eat = TimeSpan.FromHours(3);

    private static ParseOutcome Parse(string message, GenericAdapterProfile? p = null) =>
        new GenericHl7Parser(p ?? new GenericAdapterProfile { Encoding = "utf-8" }, Eat).Parse("h-1", Encoding.UTF8.GetBytes(message), "c1");

    private const string Oru =
        "MSH|^~\\&|AN|LAB|||20261103101500||ORU^R01|77|P|2.3.1\r" +
        "PID|1||MRN1\r" +
        "OBR|1||261103000202|^CHEM|||20261103101000\r" +
        "OBX|1|NM|CRP^C-reactive protein||12.5|mg/L|0-5|H~>|||F|||||op7\r" +
        "NTE|1||hemolysed\r" +
        "OBX|2|NM|ALB^Albumin||40|g/L|35-52|N|||C|||20261103101200\r" +
        "OBX|3|ED|HIST^Histogram||^Image^PNG^Base64^AAAA||||||F\r";

    [Fact]
    public void Default_layout_reads_obx_results_under_obr()
    {
        var outcome = Parse(Oru);
        Assert.True(outcome.Success, outcome.Error);
        var m = Assert.Single(outcome.Messages);
        Assert.Equal("261103000202", m.SampleId);
        Assert.Equal(2, m.Results.Count); // embedded data is not a result
        var crp = m.Results[0];
        Assert.Equal("CRP", crp.InstrumentTestCode);
        Assert.Equal(12.5m, crp.NumericValue);
        Assert.Equal("mg/L", crp.Units);
        Assert.Equal("0-5", crp.ReferenceRangeText);
        Assert.Equal(["H", ">", "note:hemolysed"], crp.InstrumentFlags);
        Assert.Equal("op7", crp.OperatorId);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 10, 10, 0, Eat), crp.RunAt); // OBR-7 when OBX-14 is empty
        var alb = m.Results[1];
        Assert.Empty(alb.InstrumentFlags);
        Assert.True(alb.IsRerun);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 10, 12, 0, Eat), alb.RunAt);
    }

    [Fact]
    public void Several_orders_in_one_message_keep_their_own_sample()
    {
        var msg = "MSH|^~\\&|AN||||20261103||ORU^R01|1|P|2.5\r" +
                  "ORC|RE|A1\rOBR|1|A1\rOBX|1|NM|K||4.1|mmol/L\r" +
                  "ORC|RE|B2\rOBR|2|B2\rOBX|1|NM|K||3.9|mmol/L\r";
        var p = new GenericAdapterProfile { Hl7SampleId = "ORC-2", Hl7SampleIdFallback = null };
        var outcome = Parse(msg, p);
        Assert.Equal(["A1", "B2"], outcome.Messages.Select(m => m.SampleId));
    }

    [Fact]
    public void Qc_by_processing_id_or_prefix()
    {
        var qc = Oru.Replace("|77|P|", "|77|Q|", StringComparison.Ordinal);
        Assert.Equal(MessageKind.Qc, Assert.Single(Parse(qc).Messages).Kind);
        Assert.Equal(MessageKind.PatientResult, Assert.Single(Parse(qc, new GenericAdapterProfile { Hl7QcWhenProcessingIdQ = false }).Messages).Kind);
        Assert.Equal(MessageKind.Qc, Assert.Single(Parse(Oru, new GenericAdapterProfile { QcSampleIdPrefixes = ["2611"] }).Messages).Kind);
    }

    [Fact]
    public void Configured_query_message_is_a_query()
    {
        var q = Assert.Single(Parse("MSH|^~\\&|AN||||20261103||QRY^Q02|9|P|2.3.1\rQRD|20261103|R|D|1|||1^RD|261103000202|OTH\r",
            new GenericAdapterProfile { Hl7QueryMessage = "QRY^Q02", Hl7QuerySampleId = "QRD-8" }).Messages);
        Assert.Equal(MessageKind.Query, q.Kind);
        Assert.Equal("261103000202", q.SampleId);
        Assert.Equal("9", q.Query!.SequenceNumber);
    }

    [Fact]
    public void Obx_without_obr_fails()
    {
        Assert.False(Parse("MSH|^~\\&|AN||||20261103||ORU^R01|1|P|2.3.1\rOBX|1|NM|K||4\r").Success);
    }

    [Fact]
    public void Replies_follow_the_profile()
    {
        var p = new GenericAdapterProfile { HostName = "LIS", ReceiverName = "AN", Hl7Version = "2.5" };
        var now = new DateTimeOffset(2026, 11, 3, 10, 0, 0, Eat);
        var ack = GenericHl7Messages.Ack(p, "AA", "77", "P", "R01", now);
        Assert.StartsWith("MSH|^~\\&|LIS||AN||20261103100000||ACK^R01|", ack);
        Assert.Contains("|P|2.5\r", ack);
        Assert.EndsWith("MSA|AA|77\r", ack);

        var order = new InstrumentOrder("261103000202", "routine", ["CRP", "ALB"]);
        var reply = GenericHl7Messages.QueryReply(p, "9", "261103000202", order, now);
        Assert.Contains("||ORR^O02|", reply);
        Assert.Contains("MSA|AA|9\rPID|1\rORC|OK|261103000202\rOBR|1|261103000202||CRP|R\rOBR|2|261103000202||ALB|R\r", reply);
        Assert.EndsWith("MSA|AA|9\r", GenericHl7Messages.QueryReply(p, "9", "X", null, now));

        var push = GenericHl7Messages.Order(p, order, now);
        Assert.Contains("||ORM^O01|", push);
        Assert.Contains("ORC|NW|261103000202\r", push);
    }
}

public class AnalyzerDefinitionTests
{
    private static AnalyzerDefinition Valid(string protocol = "astm") => new()
    {
        InstrumentId = "sysmex-xn-1",
        Model = "Sysmex XN-550",
        Protocol = protocol,
        Transport = new TransportOptions { Mode = TransportMode.TcpClient, Host = "10.0.0.5", Port = 5000 },
    };

    [Fact]
    public void A_complete_definition_has_no_errors() =>
        Assert.Empty(Valid().Validate([], []));

    [Fact]
    public void Problems_are_listed()
    {
        var d = Valid("hl7");
        d.InstrumentId = "Bad ID!";
        d.Model = " ";
        d.Transport = new TransportOptions { Mode = TransportMode.Serial, PortName = "", BaudRate = 9600, Parity = "Sideways" };
        d.Profile.Hl7TestCode = "OBR-4";
        d.Profile.Hl7QueryMessage = "ORM";
        var errors = string.Join(" | ", d.Validate([], []));
        Assert.Contains("Analyzer ID", errors);
        Assert.Contains("Model is required", errors);
        Assert.Contains("Serial port name", errors);
        Assert.Contains("Parity", errors);
        Assert.Contains("Test code: must point into OBX", errors);
        Assert.Contains("Query message must look like", errors);
    }

    [Fact]
    public void Configured_ids_and_used_ports_are_refused()
    {
        var d = Valid();
        d.InstrumentId = "e411-1";
        Assert.Contains(d.Validate([], ["e411-1"]), e => e.Contains("service configuration"));

        var server = Valid("hl7");
        server.Transport = new TransportOptions { Mode = TransportMode.TcpServer, Host = "0.0.0.0", Port = 5100 };
        var bc = new InstrumentConfig { Id = "bc5000-1", Transport = new TransportOptions { Mode = TransportMode.TcpServer, Port = 5100 } };
        Assert.Contains(server.Validate([bc], []), e => e.Contains("already used by bc5000-1"));
    }

    [Fact]
    public void Json_round_trip_keeps_everything()
    {
        var d = Valid("hl7");
        d.Profile.Worklist = WorklistMode.Both;
        d.Profile.IgnoredTestCodes = ["08001"];
        d.UtcOffset = TimeSpan.FromHours(-5.5);
        var json = d.ToJson();
        Assert.Contains("\"Both\"", json);
        var back = AnalyzerDefinition.FromJson(json);
        Assert.Equal(d.ToJson(), back.ToJson());
        var cfg = back.ToConfig(Guid.NewGuid(), 3, enabled: true);
        Assert.Equal(GenericHl7Parser.Id, cfg.Parser);
        Assert.True(cfg.HostQuery);
        Assert.True(cfg.IsRegistered);
        Assert.IsType<GenericHl7Parser>(ParserRegistry.Create(cfg));
        Assert.True(OrderDownload.Supports(cfg));
    }
}

/// <summary>The HL7 link with a registered analyzer's dialect: configured ACK, query reply and pushed orders.</summary>
public sealed class GenericHl7LinkTests : IAsyncLifetime
{
    private TcpListener _listener = null!;
    private TcpClient _instrumentSide = null!;
    private TcpClient _hostSide = null!;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private readonly FakeJournal _journal = new();
    private readonly LinkHealth _health = new() { InstrumentId = "h-1" };
    private Task _session = Task.CompletedTask;
    private Bc5000Simulator _sim = null!; // used as a plain MLLP peer
    private readonly GenericAdapterProfile _profile = new() { Encoding = "utf-8", HostName = "LIS", Worklist = WorklistMode.Both };

    public async Task InitializeAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _instrumentSide = new TcpClient();
        var accept = _listener.AcceptTcpClientAsync();
        await _instrumentSide.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        _hostSide = await accept;
        _sim = new Bc5000Simulator(_instrumentSide.GetStream(), TimeSpan.FromSeconds(5));
        _session = Task.Run(() => new Hl7LinkSession("h-1", _journal, new FixedResponder(null), _health, TimeProvider.System, NullLogger.Instance,
            new GenericHl7Dialect(_profile), new UTF8Encoding(false), allowPush: true).RunAsync(_hostSide.GetStream(), _cts.Token));
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try { await _session; } catch { /* cancelled */ }
        _instrumentSide.Dispose(); _hostSide.Dispose(); _listener.Stop();
    }

    [Fact]
    public async Task Result_is_stored_and_acknowledged_with_the_configured_header()
    {
        var reply = await _sim.SendAsync("MSH|^~\\&|AN||||20261103||ORU^R01|55|P|2.5\rOBR|1||S1\rOBX|1|NM|K||4.1|mmol/L\r");
        Assert.Equal("AA", Bc5000Simulator.AckCode(reply));
        Assert.StartsWith("MSH|^~\\&|LIS||", reply);
        Assert.Contains("ACK^R01", reply);
        Assert.Contains("MSA|AA|55", reply);
        Assert.Single(_journal.Messages);
    }

    [Fact]
    public async Task Query_without_an_order_gets_the_empty_reply()
    {
        var reply = await _sim.SendAsync("MSH|^~\\&|AN||||20261103||ORM^O01|56|P|2.3.1\rORC|RF||S1\r");
        Assert.Contains("ORR^O02", reply);
        Assert.EndsWith("MSA|AA|56\r", reply);
    }

    [Fact]
    public async Task Pushed_order_is_sent_and_the_analyzer_ack_is_not_stored()
    {
        await WaitUntil(() => _health.EnqueueOutbound is not null);
        var order = GenericHl7Messages.Order(_profile, new InstrumentOrder("S9", "routine", ["K"]), DateTimeOffset.UtcNow);
        _health.EnqueueOutbound!("order.download", order, "corr-1");
        var received = await _sim.ReceiveAsync();
        Assert.Contains("ORC|NW|S9", received);
        await WaitUntil(() => _journal.Outbound.Count == 1);
        Assert.Equal(("order.download", "Sent"), (_journal.Outbound.Single().Kind, _journal.Outbound.Single().Outcome));

        // The analyzer's ACK: no reply, nothing in the inbox.
        await _instrumentSide.GetStream().WriteAsync(Mllp.Frame("MSH|^~\\&|AN||||20261103||ACK^O01|1|P|2.3.1\rMSA|AA|x\r"u8.ToArray()));
        var next = await _sim.SendAsync("MSH|^~\\&|AN||||20261103||ORU^R01|57|P|2.5\rOBR|1||S1\rOBX|1|NM|K||4.1|mmol/L\r");
        Assert.Contains("MSA|AA|57", next); // the first reply on the wire answers the result, not the ACK
        Assert.Single(_journal.Messages);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(condition());
    }
}
