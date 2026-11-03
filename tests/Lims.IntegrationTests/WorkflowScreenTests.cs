using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Lims.Audit;
using Lims.Clinical;
using Lims.Host.Identity;
using Lims.Instruments;
using Lims.Platform;
using Lims.Sim.Bc5000;
using Lims.Sim.E411;

namespace Lims.IntegrationTests;

/// <summary>Autoverification, recollection, rollback, signed audit export, order resend, link pause, and a render check of every screen.</summary>
public sealed partial class EndToEndTests
{
    [Fact]
    public async Task Autoverification_needs_an_approved_rule_qc_and_a_clean_result()
    {
        var av = f.Get<AutoverificationService>();
        await Assert.ThrowsAsync<WorkflowException>(() => av.ProposeAsync(new AutoverifyRuleProposal("GLU", "c111-1", null, null, 24, " "), "labmgr1", default));
        var ruleId = await av.ProposeAsync(new AutoverifyRuleProposal("GLU", "c111-1", 3.9m, 5.6m, 24, "VAL-2026-014"), "labmgr1", default);
        await Assert.ThrowsAsync<WorkflowException>(() => av.ApproveAsync(ruleId, "labmgr1", default)); // four eyes
        await av.ApproveAsync(ruleId, "labmgr2", default);
        try
        {
            // No QC yet: the result waits for a person.
            await using (var c = await f.OpenAsync())
                await c.ExecuteAsync("DELETE FROM clinical.qc_result WHERE instrument_id='c111-1' AND instrument_test_code='989'");
            var (o1, _, _) = await NewOrderAsync("GLU");
            var s1 = await CollectAndAcceptAsync(o1);
            await SendC111Async(C111Messages.Results(s1.Barcode, [new SimResult("989", "5.0", "mmol/L")], DateTime.Now.AddSeconds(Interlocked.Increment(ref _seq))));
            var r1 = await WaitForResultAsync(s1.Barcode, "GLU");
            await using (var c = await f.OpenAsync())
            {
                var skipped = await LimsFixture.WaitFor(async () => await c.ExecuteScalarAsync<string?>(
                    "SELECT reason FROM audit.audit_event WHERE entity_id=@id AND action='result.autoverify_skipped'", new { id = r1.ToString() }));
                Assert.Contains("no QC", skipped);
            }

            // Unflagged QC, then a clean result inside the limits: released by the rule and delivered.
            await SendC111Async(C111Messages.Qc(302, "989", "5.3", "mmol/L", DateTime.Now.AddMinutes(-Interlocked.Increment(ref _seq))));
            await LimsFixture.WaitFor<bool?>(async () =>
            {
                await using var c = await f.OpenAsync();
                return await c.ExecuteScalarAsync<int>("SELECT count(*) FROM clinical.qc_result WHERE instrument_id='c111-1' AND instrument_test_code='989'") > 0;
            }, what: "QC");
            var (o2, hmisOrder2, _) = await NewOrderAsync("GLU");
            var s2 = await CollectAndAcceptAsync(o2);
            await SendC111Async(C111Messages.Results(s2.Barcode, [new SimResult("989", "4.8", "mmol/L")], DateTime.Now.AddSeconds(Interlocked.Increment(ref _seq))));
            var found = await LimsFixture.WaitFor(async () =>
            {
                await using var c = await f.OpenAsync();
                return await c.QuerySingleOrDefaultAsync<(Guid Id, string Actor, Guid? Rule)?>("""
                    SELECT r.id, v.actor, v.autoverify_rule_id FROM clinical.result r
                    JOIN clinical.review v ON v.result_id=r.id AND v.action='approve'
                    JOIN clinical.specimen_test st ON st.id=r.specimen_test_id JOIN clinical.specimen s ON s.id=st.specimen_id
                    WHERE s.barcode=@b AND r.status='approved'
                    """, new { b = s2.Barcode });
            }, what: "autoverified result");
            Assert.True(found.HasValue);
            var r2 = found.GetValueOrDefault();
            Assert.Equal("autoverify:GLU:v1", r2.Actor);
            Assert.Equal(ruleId, r2.Rule);
            await WaitForDeliveryAsync(r2.Id);
            Assert.Equal("autoverify:GLU:v1", f.HmisState.AllResults().Single(r => r.Item.OrderId == hmisOrder2).Item.ApprovedBy);

            // Outside the limits: waits for a person.
            var (o3, _, _) = await NewOrderAsync("GLU");
            var s3 = await CollectAndAcceptAsync(o3);
            await SendC111Async(C111Messages.Results(s3.Barcode, [new SimResult("989", "7.4", "mmol/L", "H")], DateTime.Now.AddSeconds(Interlocked.Increment(ref _seq))));
            var r3 = await WaitForResultAsync(s3.Barcode, "GLU");
            await using (var c = await f.OpenAsync())
                Assert.Equal("received", await c.ExecuteScalarAsync<string>("SELECT status FROM clinical.result WHERE id=@r3", new { r3 }));
        }
        finally
        {
            await av.DisableAsync(ruleId, "labmgr1", "end of test", default);
        }
    }

    [Fact]
    public async Task Rejected_specimen_recollection_links_the_new_tube()
    {
        var (orderId, _, _) = await NewOrderAsync("TSH");
        var first = await CollectAndAcceptAsync(orderId);
        var specimens = f.Get<SpecimenService>();
        await specimens.RejectAsync(first.Accession, "tech1", "haemolysed", default);
        await specimens.RequestRecollectionAsync(first.Accession, "tech1", "haemolysed, ward informed", default);
        var second = Assert.Single(await specimens.CollectAsync(orderId, "tech1", default));
        Assert.NotEqual(first.Accession, second.Accession);
        await using var c = await f.OpenAsync();
        Assert.Equal(first.SpecimenId, await c.ExecuteScalarAsync<Guid?>("SELECT recollection_of FROM clinical.specimen WHERE id=@id", new { id = second.SpecimenId }));
        Assert.Equal("recollection_requested", await c.ExecuteScalarAsync<string>("SELECT status FROM clinical.specimen WHERE id=@id", new { id = first.SpecimenId }));
    }

    [Fact]
    public async Task Catalog_and_mapping_rollback_needs_a_second_approver()
    {
        var catalog = f.Get<CatalogService>();
        await catalog.PublishAsync(new CatalogEntry("RBK", "Rollback test v1", null, "U/L", 0, 10m, 50m, null, null, "serum", "c111-1"), "labmgr1", default);
        await catalog.ProposeAsync(new CatalogEntry("RBK", "Rollback test v2", null, "U/L", 0, 10m, 60m, null, null, "serum", "c111-1"), "labmgr1", default);
        await catalog.ApproveAsync("RBK", "labmgr2", default);
        await Assert.ThrowsAsync<WorkflowException>(() => catalog.ProposeRollbackAsync("RBK", 2, "labmgr1", default)); // already active
        var v3 = await catalog.ProposeRollbackAsync("RBK", 1, "labmgr1", default);
        Assert.Equal(3, v3);
        await Assert.ThrowsAsync<WorkflowException>(() => catalog.ApproveAsync("RBK", "labmgr1", default));
        await catalog.ApproveAsync("RBK", "labmgr2", default);
        await using (var c = await f.OpenAsync())
            Assert.Equal(("Rollback test v1", 50m), await c.QuerySingleAsync<(string, decimal)>("SELECT name, ref_high FROM clinical.test_catalog WHERE code='RBK' AND status='active'"));

        var mappings = f.Get<MappingService>();
        var m1 = await mappings.ProposeAsync(new MappingProposal("c111-1", "7001", "RBK", "numeric", 1m, null, true), "labmgr1", default);
        await mappings.ApproveAsync(m1, "labmgr2", default);
        var m2 = await mappings.ProposeAsync(new MappingProposal("c111-1", "7001", "RBK", "numeric", 10m, null, true), "labmgr1", default);
        await mappings.ApproveAsync(m2, "labmgr2", default);
        var m3 = await mappings.ProposeRollbackAsync(m1, "labmgr1", default);
        await mappings.ApproveAsync(m3, "labmgr2", default);
        await using (var c = await f.OpenAsync())
            Assert.Equal(1m, await c.ExecuteScalarAsync<decimal>("SELECT unit_factor FROM instruments.test_mapping WHERE instrument_code='7001' AND status='active'"));
    }

    [Fact]
    public async Task Signed_audit_export_verifies_and_detects_tampering()
    {
        var (orderId, _, mrn) = await NewOrderAsync("TSH");
        await CollectAndAcceptAsync(orderId);
        foreach (var format in new[] { "csv", "json" })
        {
            var (zip, name) = await f.Get<AuditPackage>().ExportAsync(new AuditFilter(Patient: mrn), format, "auditor1", default);
            Assert.EndsWith(".zip", name);
            var key = "integration-test-signing-key-0123456789abcdef";
            Assert.True(AuditPackage.Verify(new MemoryStream(zip), key).Valid);
            Assert.False(AuditPackage.Verify(new MemoryStream(zip), key + "x").Valid);

            // Change one byte of the data file inside the archive.
            using var ms = new MemoryStream();
            ms.Write(zip);
            using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
            {
                var entry = archive.GetEntry($"audit.{format}")!;
                string text;
                using (var r = new StreamReader(entry.Open())) text = r.ReadToEnd();
                entry.Delete();
                using var w = new StreamWriter(archive.CreateEntry($"audit.{format}").Open());
                w.Write(text.Replace("order.registered", "order.registereD", StringComparison.Ordinal));
            }
            ms.Position = 0;
            Assert.False(AuditPackage.Verify(ms, key).Valid);
        }
        await using var c = await f.OpenAsync();
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT count(*) FROM audit.audit_event WHERE action='audit.exported' AND actor='auditor1'") >= 2);
    }

    [Fact]
    public async Task Resend_order_downloads_to_c111_and_explains_bc5000()
    {
        var (orderId, _, _) = await NewOrderAsync("GLU");
        var s = await CollectAndAcceptAsync(orderId);
        await f.Get<OrderResendService>().ResendAsync("c111-1", s.Barcode, "tech1", default);
        var msg = await f.C111.ReceiveAsync();
        Assert.NotNull(msg);
        Assert.Contains("TSDWN^BATCH", msg);
        Assert.Contains($"O|1|{s.Barcode}||^^^989|R||||||A", msg);
        await Assert.ThrowsAsync<WorkflowException>(() => f.Get<OrderResendService>().ResendAsync("bc5000-1", s.Barcode, "tech1", default));
    }

    [Fact]
    public async Task Paused_hl7_link_refuses_then_accepts_after_resume()
    {
        var health = f.Get<LinkHealthRegistry>().Get("bc5000-1");
        health.Paused = true;
        try
        {
            var ack = await f.Bc5000.SendAsync(f.Bc5000.ResultMessage("000000000003", Bc5000Simulator.NormalCbc().Take(1), DateTime.Now));
            Assert.Equal("AE", Bc5000Simulator.AckCode(ack));
        }
        finally { health.Paused = false; }
        var ok = await f.Bc5000.SendAsync(f.Bc5000.ResultMessage("000000000003", Bc5000Simulator.NormalCbc().Take(1), DateTime.Now));
        Assert.Equal("AA", Bc5000Simulator.AckCode(ok));
    }

    [Fact]
    public async Task Every_screen_renders_for_a_user_with_all_roles()
    {
        var users = f.Get<UserStore>();
        const string password = "screens test password 1";
        await users.CreateAsync("screens", "Screens", password, Roles.All, "admin", default);
        var enroll = await users.SignInAsync("screens", password, null, "127.0.0.1", default);
        var secret = Totp.Base32Decode(enroll.NewTotpSecret!);
        Assert.Equal(LoginStatus.Ok, (await users.SignInAsync("screens", password,
            Totp.Compute(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), "127.0.0.1", default)).Status);

        using var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(f.AppUrl) };
        var login = await http.GetStringAsync("/Login");
        var token = Regex.Match(login, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var post = await http.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "screens", ["Password"] = password,
            ["Code"] = Totp.Compute(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30),
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal("/", post.RequestMessage!.RequestUri!.AbsolutePath);

        var (orderId, _, _) = await NewOrderAsync("CBC");
        var s = await CollectAndAcceptAsync(orderId);
        foreach (var page in new[] { "/", "/Orders", "/Specimens", $"/Specimens?accession={s.Accession}", "/Analyzers", "/LoadList?instrument=bc5000-1",
                     "/Review", "/Exceptions", "/Deliveries", "/Reconciliation", "/Downtime", "/Configuration", "/Configuration?history=true",
                     "/AuditLog", "/AuditLog?action=specimen.&instrument=c111-1" })
        {
            var res = await http.GetAsync(page);
            var body = await res.Content.ReadAsStringAsync();
            Assert.True(res.IsSuccessStatusCode, $"{page}: {(int)res.StatusCode} {body[..Math.Min(500, body.Length)]}");
            Assert.DoesNotContain("/Login", res.RequestMessage!.RequestUri!.AbsolutePath);
        }
        Assert.Contains(s.Accession, await http.GetStringAsync("/LoadList?instrument=bc5000-1"));
    }
}
