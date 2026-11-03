using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Dapper;
using Lims.Clinical;
using Lims.Host.Identity;
using Lims.Instruments;
using Lims.Instruments.Astm;
using Lims.Instruments.Generic;
using Lims.Instruments.Hl7;
using Lims.Instruments.Registration;
using Lims.Platform;
using Lims.Sim.Bc5000;
using Lims.Sim.E411;

namespace Lims.IntegrationTests;

/// <summary>
/// Analyzers registered from the screens (no built-in adapter): maker-checker approval, the link starting without a
/// restart, host query, order push, results, and disabling. Simulated traffic only.
/// </summary>
public sealed partial class EndToEndTests
{
    private async Task MapAsync(string instrumentId, string code, string catalogCode, string unit)
    {
        await f.Get<CatalogService>().PublishAsync(new CatalogEntry(catalogCode, $"{catalogCode} test", null, unit, 1, 0m, 10m, null, null, "serum", instrumentId), "labmgr1", default);
        var mappings = f.Get<MappingService>();
        var id = await mappings.ProposeAsync(new MappingProposal(instrumentId, code, catalogCode, "numeric", 1m, null, true), "labmgr1", default);
        await mappings.ApproveAsync(id, "labmgr2", default);
    }

    [Fact]
    public async Task Registered_astm_analyzer_starts_only_after_a_second_lab_manager_approves()
    {
        var deviceServer = new TcpListener(IPAddress.Loopback, 0);
        deviceServer.Start();
        try
        {
            var port = ((IPEndPoint)deviceServer.LocalEndpoint).Port;
            var accept = deviceServer.AcceptTcpClientAsync();
            var svc = f.Get<AnalyzerDefinitionService>();
            var id = await svc.ProposeAsync(new AnalyzerDefinition
            {
                InstrumentId = "chem-astm-1",
                Model = "Example chemistry analyzer",
                SoftwareVersion = "2.1",
                Protocol = "astm",
                Transport = new TransportOptions { Mode = TransportMode.TcpClient, Host = "127.0.0.1", Port = port },
                Link = new AstmLinkOptions { IdlePoll = TimeSpan.FromMilliseconds(50) },
                Profile = new GenericAdapterProfile { Worklist = WorklistMode.Both, HostName = "LIS", ReceiverName = "CHEM" },
            }, "labmgr1", default);

            // Nothing runs on a proposal, and the proposer cannot approve it.
            Assert.Null(f.Get<InstrumentDirectory>().Find("chem-astm-1"));
            var self = await Assert.ThrowsAsync<WorkflowException>(() => svc.ApproveAsync(id, "labmgr1", default));
            Assert.Contains("different user", self.Message);
            await Task.Delay(300);
            Assert.False(accept.IsCompleted);

            await svc.ApproveAsync(id, "labmgr2", default);
            using var connection = await accept.WaitAsync(TimeSpan.FromSeconds(30));
            var sim = new E411Simulator(connection.GetStream(), TimeSpan.FromSeconds(20));
            await using (var c = await f.OpenAsync())
            {
                Assert.Equal("unverified", await c.ExecuteScalarAsync<string>("SELECT verification_status FROM instruments.instrument WHERE id='chem-astm-1'"));
                var actions = (await c.QueryAsync<string>("SELECT action FROM audit.audit_event WHERE entity_id LIKE 'chem-astm-1:%' ORDER BY seq")).ToList();
                Assert.Equal(["analyzer.proposed", "analyzer.approved"], actions);
            }

            await MapAsync("chem-astm-1", "CRP", "CRP", "mg/L");
            var (orderId, _, _) = await NewOrderAsync("CRP");
            var s = await CollectAndAcceptAsync(orderId);

            // Host query: the analyzer asks for the tube, the middleware answers with the mapped instrument code.
            Assert.Equal(SendStatus.Accepted, await sim.SendAsync($"H|\\^&|||CHEM\rQ|1|^{s.Barcode}||ALL||||||||O\rL|1|N\r"));
            var reply = await sim.ReceiveAsync();
            Assert.Contains($"O|1|{s.Barcode}||^^^CRP|R|", reply);
            Assert.StartsWith("H|\\^&|||LIS|||||CHEM|", reply);

            // Order push on request.
            await f.Get<OrderResendService>().ResendAsync("chem-astm-1", s.Barcode, "tech1", default);
            Assert.Contains($"O|1|{s.Barcode}||^^^CRP|", await sim.ReceiveAsync());

            var at = DateTime.Now.AddSeconds(Interlocked.Increment(ref _seq));
            Assert.Equal(SendStatus.Accepted, await sim.SendAsync(
                $"H|\\^&|||CHEM|||||||P|1|{at:yyyyMMddHHmmss}\rP|1\rO|1|{s.Barcode}||^^^CRP|R||||||N||||||||||||||F\r" +
                $"R|1|^^^CRP|12.5|mg/L|0^5|H||F||op1||{at:yyyyMMddHHmmss}\rL|1|N\r"));
            var crp = await WaitForResultAsync(s.Barcode, "CRP");
            await using (var c = await f.OpenAsync())
                Assert.Equal("12.5", await c.ExecuteScalarAsync<string>("SELECT value_text FROM clinical.result WHERE id=@crp", new { crp }));

            // Disabling stops the link at once.
            await svc.DisableAsync("chem-astm-1", "labmgr1", "moved to the annex", default);
            var buffer = new byte[16];
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.Equal(0, await connection.GetStream().ReadAsync(buffer, readTimeout.Token));
            Assert.False(f.Get<InstrumentDirectory>().Find("chem-astm-1")!.Enabled);
            await Assert.ThrowsAsync<WorkflowException>(() => svc.DisableAsync("chem-astm-1", "labmgr1", "again", default));
        }
        finally { deviceServer.Stop(); }
    }

    [Fact]
    public async Task Registered_hl7_analyzer_answers_queries_pushes_orders_and_files_results()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var svc = f.Get<AnalyzerDefinitionService>();
        var id = await svc.ProposeAsync(new AnalyzerDefinition
        {
            InstrumentId = "lyte-hl7-1",
            Model = "Example electrolyte analyzer",
            Protocol = "hl7",
            Transport = new TransportOptions { Mode = TransportMode.TcpServer, Host = "127.0.0.1", Port = port },
            Profile = new GenericAdapterProfile { Worklist = WorklistMode.Both, Encoding = "utf-8", HostName = "LIS" },
        }, "labmgr1", default);
        await svc.ApproveAsync(id, "labmgr2", default);
        await MapAsync("lyte-hl7-1", "K", "K", "mmol/L");

        using var client = new TcpClient();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { await client.ConnectAsync(IPAddress.Loopback, port); break; }
            catch (SocketException) when (DateTime.UtcNow < until) { await Task.Delay(200); }
        }
        var sim = new Bc5000Simulator(client.GetStream(), TimeSpan.FromSeconds(20)); // a plain MLLP peer here
        var (orderId, _, _) = await NewOrderAsync("K");
        var s = await CollectAndAcceptAsync(orderId);

        var orr = await sim.SendAsync($"MSH|^~\\&|LYTE||||{DateTime.Now:yyyyMMddHHmmss}||ORM^O01|q1|P|2.3.1\rORC|RF||{s.Barcode}\r");
        Assert.Contains("ORR^O02", orr);
        Assert.Contains($"ORC|OK|{s.Barcode}\rOBR|1|{s.Barcode}||K|R\r", orr);

        await f.Get<OrderResendService>().ResendAsync("lyte-hl7-1", s.Barcode, "tech1", default);
        var pushed = await sim.ReceiveAsync();
        Assert.Contains($"ORC|NW|{s.Barcode}", pushed);
        await client.GetStream().WriteAsync(Mllp.Frame("MSH|^~\\&|LYTE||||20261103||ACK^O01|a1|P|2.3.1\rMSA|AA|x\r"u8.ToArray()));

        var ack = await sim.SendAsync($"MSH|^~\\&|LYTE||||{DateTime.Now:yyyyMMddHHmmss}||ORU^R01|r1|P|2.3.1\r" +
                                      $"OBR|1||{s.Barcode}\rOBX|1|NM|K^Potassium||4.1|mmol/L|3.5-5.1|N|||F\r");
        Assert.Equal("AA", Bc5000Simulator.AckCode(ack));
        Assert.Contains("MSA|AA|r1", ack);
        var k = await WaitForResultAsync(s.Barcode, "K");
        await using var c = await f.OpenAsync();
        Assert.Equal("4.1", await c.ExecuteScalarAsync<string>("SELECT value_text FROM clinical.result WHERE id=@k", new { k }));
        // The analyzer's ACK to the pushed order is not stored as an analyzer message.
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM messaging.inbox_message WHERE channel='lyte-hl7-1' AND convert_from(raw,'UTF8') LIKE '%ACK^O01%'"));
        await svc.DisableAsync("lyte-hl7-1", "labmgr1", "test done", default);
    }

    [Fact]
    public async Task Analyzer_definitions_are_versioned_and_rolled_back_with_approval()
    {
        var svc = f.Get<AnalyzerDefinitionService>();
        AnalyzerDefinition Def(int port) => new()
        {
            InstrumentId = "versioned-1", Model = "Example", Protocol = "astm",
            Transport = new TransportOptions { Mode = TransportMode.TcpClient, Host = "127.0.0.1", Port = port },
        };
        var v1 = await svc.ProposeAsync(Def(1), "labmgr1", default);
        await svc.ApproveAsync(v1, "labmgr2", default);
        var v2 = await svc.ProposeAsync(Def(2), "labmgr2", default);
        Assert.Equal(1, f.Get<InstrumentDirectory>().Find("versioned-1")!.Transport.Port); // v2 waits for approval
        await svc.ApproveAsync(v2, "labmgr1", default);
        Assert.Equal(2, f.Get<InstrumentDirectory>().Find("versioned-1")!.Transport.Port);

        var rollback = await svc.ProposeCopyAsync(v1, "labmgr1", default);
        await svc.ApproveAsync(rollback, "labmgr2", default);
        var active = f.Get<InstrumentDirectory>().Find("versioned-1")!;
        Assert.Equal(1, active.Transport.Port);
        Assert.Equal(3, active.DefinitionVersion);

        // A proposal for an ID from the service configuration, or one listening on a port in use, is refused.
        var clash = Def(1);
        clash.InstrumentId = "e411-1";
        await Assert.ThrowsAsync<WorkflowException>(() => svc.ProposeAsync(clash, "labmgr1", default));
        var server = Def(1);
        server.InstrumentId = "port-clash";
        server.Protocol = "hl7";
        server.Transport = new TransportOptions { Mode = TransportMode.TcpServer, Host = "127.0.0.1", Port = f.Get<InstrumentDirectory>().Find("bc5000-1")!.Transport.Port };
        var e = await Assert.ThrowsAsync<WorkflowException>(() => svc.ProposeAsync(server, "labmgr1", default));
        Assert.Contains("bc5000-1", e.Message);

        await svc.DisableAsync("versioned-1", "labmgr1", "test done", default);
        var again = await svc.ProposeCopyAsync(rollback, "labmgr1", default);
        await svc.WithdrawAsync(again, "labmgr1", default);
        Assert.False(f.Get<InstrumentDirectory>().Find("versioned-1")!.Enabled);
    }

    [Fact]
    public async Task Analyzer_setup_screen_checks_a_sample_message_and_proposes()
    {
        var users = f.Get<UserStore>();
        const string password = "setup screen password 1";
        await users.CreateAsync("setup-lm", "Setup LM", password, [Roles.LabManager], "admin", default);
        var enroll = await users.SignInAsync("setup-lm", password, null, "127.0.0.1", default);
        var secret = Totp.Base32Decode(enroll.NewTotpSecret!);
        Assert.Equal(LoginStatus.Ok, (await users.SignInAsync("setup-lm", password,
            Totp.Compute(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), "127.0.0.1", default)).Status);

        using var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(f.AppUrl) };
        static string Token(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var login = await http.GetStringAsync("/Login");
        await http.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "setup-lm", ["Password"] = password,
            ["Code"] = Totp.Compute(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30),
            ["__RequestVerificationToken"] = Token(login),
        }));

        var page = await http.GetStringAsync("/AnalyzerSetup");
        Assert.Contains("Propose an analyzer", page);
        Dictionary<string, string> Form(string token) => new()
        {
            ["Definition.InstrumentId"] = "Screen-1", ["Definition.Model"] = "Screen analyzer", ["Definition.Protocol"] = "astm",
            ["UtcOffsetText"] = "+03:00", ["Definition.Transport.Mode"] = "TcpClient", ["Definition.Transport.Host"] = "127.0.0.1",
            ["Definition.Transport.Port"] = "9", ["Definition.Profile.Worklist"] = "None", ["Definition.Profile.AstmTestCode"] = "R-3.1",
            ["IgnoredTestCodes"] = "MODE, FLAG", ["SampleMessage"] = "H|\\^&\nO|1|S77||^^^GLU\nR|1|GLU|5.4|mmol/L\nR|2|MODE|1\nL|1",
            ["__RequestVerificationToken"] = token,
        };

        var check = await http.PostAsync("/AnalyzerSetup?handler=Check", new FormUrlEncodedContent(Form(Token(page))));
        var checkedPage = await check.Content.ReadAsStringAsync();
        Assert.True(check.IsSuccessStatusCode, checkedPage[..Math.Min(500, checkedPage.Length)]);
        Assert.Contains("How the message is read", checkedPage);
        Assert.Contains("<code>S77</code>", checkedPage);
        Assert.Contains("<td>GLU</td>", checkedPage);
        Assert.DoesNotContain("<td>MODE</td>", checkedPage);
        await using (var c = await f.OpenAsync())
            Assert.Equal(0, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM instruments.analyzer_definition WHERE instrument_id='screen-1'"));

        var propose = await http.PostAsync("/AnalyzerSetup?handler=Propose", new FormUrlEncodedContent(Form(Token(checkedPage))));
        Assert.Contains("A different Lab Manager must approve it", await propose.Content.ReadAsStringAsync());
        await using (var c = await f.OpenAsync())
        {
            var row = await c.QuerySingleAsync("SELECT status, proposed_by, definition->'profile'->>'astmTestCode' AS code FROM instruments.analyzer_definition WHERE instrument_id='screen-1'");
            Assert.Equal("proposed", (string)row.status);
            Assert.Equal("setup-lm", (string)row.proposed_by);
            Assert.Equal("R-3.1", (string)row.code);
        }
        var defId = (await f.Get<AnalyzerDefinitionService>().ListAsync(false, default)).Single(r => r.InstrumentId == "screen-1").Id;
        await f.Get<AnalyzerDefinitionService>().WithdrawAsync(defId, "setup-lm", default);
    }
}
