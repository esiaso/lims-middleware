using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using Lims.Audit;
using Lims.Clinical;
using Lims.Contracts;
using Lims.Hmis;
using Lims.Host.Identity;
using Lims.Platform;
using Lims.Sim.E411;

namespace Lims.IntegrationTests;

[Collection("lims")]
public sealed class EndToEndTests(LimsFixture f)
{
    private static int _seq;

    private async Task<(Guid OrderId, string OrderHmisId, string Mrn)> NewOrderAsync(params string[] tests)
    {
        var n = Interlocked.Increment(ref _seq);
        var id = $"ORD-{Guid.NewGuid():N}"[..16];
        var mrn = $"MRN-TEST-{n:D4}{Random.Shared.Next(1000, 9999)}";
        f.HmisState.UpsertOrder(new HmisOrder(id, DateTimeOffset.UtcNow, "active", "routine",
            new HmisPatient(mrn, "Jane", "TEST", "F", new DateOnly(1990, 1, 1), null, null),
            new HmisEncounter($"ENC-{id}", "outpatient", "OPD"), "Dr TEST",
            tests.Select((t, i) => new HmisOrderLine($"{id}-L{i + 1}", t, null, t, "active")).ToList()));
        var orderId = await LimsFixture.WaitFor<Guid?>(async () =>
        {
            await using var c = await f.OpenAsync();
            return await c.ExecuteScalarAsync<Guid?>("SELECT id FROM clinical.lab_order WHERE hmis_order_id=@id", new { id });
        }, what: "order registration");
        return (orderId!.Value, id, mrn);
    }

    private async Task<SpecimenCreated> CollectAndAcceptAsync(Guid orderId)
    {
        var specimens = await f.Get<SpecimenService>().CollectAsync(orderId, "tech1", default);
        var s = Assert.Single(specimens);
        await f.Get<SpecimenService>().AcceptAsync(s.Accession, "tech1", default);
        return s;
    }

    private async Task<Guid> WaitForResultAsync(string barcode, string code)
    {
        var id = await LimsFixture.WaitFor<Guid?>(async () =>
        {
            await using var c = await f.OpenAsync();
            return await c.ExecuteScalarAsync<Guid?>("""
                SELECT r.id FROM clinical.result r JOIN clinical.specimen_test st ON st.id=r.specimen_test_id
                JOIN clinical.specimen s ON s.id=st.specimen_id JOIN clinical.order_test ot ON ot.id=st.order_test_id
                WHERE s.barcode=@barcode AND ot.catalog_code=@code AND r.status IN ('received','reviewed') ORDER BY r.version DESC LIMIT 1
                """, new { barcode, code });
        }, what: $"result {code}");
        return id!.Value;
    }

    private async Task<(string Status, string? Ref)> WaitForDeliveryAsync(Guid resultId, string status = "confirmed")
    {
        return await LimsFixture.WaitFor<(string, string?)?>(async () =>
        {
            await using var c = await f.OpenAsync();
            var row = await c.QuerySingleOrDefaultAsync<(string Status, string? Ref)?>(
                "SELECT status, external_ref FROM messaging.outbox_message WHERE aggregate_id=@resultId", new { resultId });
            return row is { } r && r.Status == status ? r : null;
        }, TimeSpan.FromSeconds(30), "delivery") ?? default;
    }

    private Task SendResultAsync(string sampleId, params SimResult[] results) =>
        SendAsync(E411Simulator.ResultMessage(sampleId, results, DateTime.Now.AddSeconds(Interlocked.Increment(ref _seq))));

    private async Task SendAsync(string message) => Assert.Equal(SendStatus.Accepted, await f.Analyzer.SendAsync(message));

    [Fact]
    public async Task Order_to_confirmed_HMIS_delivery_end_to_end()
    {
        var (orderId, hmisOrderId, mrn) = await NewOrderAsync("TSH", "FT4");
        var specimen = await CollectAndAcceptAsync(orderId);
        Assert.Contains("^BCN", specimen.Zpl);
        Assert.Contains(specimen.Barcode, specimen.Zpl);

        // Host query: the analyzer asks what to run on this tube.
        await SendAsync(E411Simulator.QueryMessage(specimen.Barcode));
        var reply = await f.Analyzer.ReceiveAsync();
        Assert.NotNull(reply);
        Assert.Contains($"O|1|{specimen.Barcode}|40^0^5^^S1^SC|^^^10^\\^^^20^|R||||||A", reply);

        // Results come back.
        await SendResultAsync(specimen.Barcode, new SimResult("10", "1.25", "mIU/L"), new SimResult("20", "<5.0", "pmol/L", "L"));
        var tsh = await WaitForResultAsync(specimen.Barcode, "TSH");
        var ft4 = await WaitForResultAsync(specimen.Barcode, "FT4");

        await using (var c = await f.OpenAsync())
        {
            var ft4Row = await c.QuerySingleAsync("SELECT value_text, comparator, abnormal_flag, is_critical FROM clinical.result WHERE id=@ft4", new { ft4 });
            Assert.Equal("<5.0", (string)ft4Row.value_text); // preserved exactly (AC7)
            Assert.Equal("<", (string)ft4Row.comparator);
            Assert.Equal("L", (string)ft4Row.abnormal_flag);
        }

        var review = f.Get<ResultReviewService>();
        await review.ReviewAsync(tsh, "tech1", null, default);
        await Assert.ThrowsAsync<WorkflowException>(() => review.ApproveAsync(tsh, "tech1", null, default)); // reviewer cannot approve
        await review.ApproveAsync(tsh, "approver1", null, default);
        await review.ApproveAsync(ft4, "approver1", null, default);

        var (_, hmisRef) = await WaitForDeliveryAsync(tsh);
        await WaitForDeliveryAsync(ft4);
        var stored = f.HmisState.AllResults().Where(r => r.Item.OrderId == hmisOrderId).ToList();
        Assert.Equal(2, stored.Count);
        var tshStored = stored.Single(r => r.Item.TestCode == "TSH");
        Assert.Equal(hmisRef, tshStored.HmisResultId);
        Assert.Equal("3016-3", tshStored.Item.Loinc);
        Assert.Equal("1.25", tshStored.Item.Value);
        Assert.Equal(mrn, tshStored.Item.Mrn);
        Assert.Equal(specimen.Accession, tshStored.Item.Accession);
        Assert.Equal("approver1", tshStored.Item.ApprovedBy);

        // AC5: one search by result id shows receipt, review, approval and HMIS confirmation.
        await using var conn = await f.OpenAsync();
        var actions = (await conn.QueryAsync<string>("SELECT action FROM audit.audit_event WHERE entity_id=@id ORDER BY seq", new { id = tsh.ToString() })).ToList();
        Assert.Equal(["result.received", "result.reviewed", "result.approved", "result.hmis_confirmed"], actions);
        Assert.True((await f.Get<AuditVerifier>().VerifyAsync(default)).Valid);
    }

    [Fact]
    public async Task Lost_HMIS_response_is_read_back_and_never_duplicated()
    {
        var (orderId, hmisOrderId, _) = await NewOrderAsync("TSH");
        var s = await CollectAndAcceptAsync(orderId);
        await SendResultAsync(s.Barcode, new SimResult("10", "2.5", "mIU/L"));
        var id = await WaitForResultAsync(s.Barcode, "TSH");

        f.HmisState.Faults.DropResponseAfterCommitNext = 1;
        await f.Get<ResultReviewService>().ApproveAsync(id, "approver1", null, default);
        await WaitForDeliveryAsync(id);

        Assert.Single(f.HmisState.AllResults(), r => r.Item.OrderId == hmisOrderId); // AC3
        await using var c = await f.OpenAsync();
        var ops = (await c.QueryAsync<string>("""
            SELECT a.operation || ':' || a.outcome FROM hmis.delivery_attempt a JOIN messaging.outbox_message m ON m.id=a.outbox_id
            WHERE m.aggregate_id=@id ORDER BY a.id
            """, new { id })).ToList();
        Assert.Equal(["submit:unknown", "read_back:confirmed"], ops);
    }

    [Fact]
    public async Task Rate_limited_delivery_is_retried_with_same_key()
    {
        var (orderId, hmisOrderId, _) = await NewOrderAsync("TSH");
        var s = await CollectAndAcceptAsync(orderId);
        await SendResultAsync(s.Barcode, new SimResult("10", "3.1", "mIU/L"));
        var id = await WaitForResultAsync(s.Barcode, "TSH");
        f.HmisState.Faults.RateLimitNext = 1;
        await f.Get<ResultReviewService>().ApproveAsync(id, "approver1", null, default);
        await WaitForDeliveryAsync(id);
        Assert.Single(f.HmisState.AllResults(), r => r.Item.OrderId == hmisOrderId);
    }

    [Fact]
    public async Task Resent_message_creates_one_result_and_rerun_creates_new_version()
    {
        var (orderId, _, _) = await NewOrderAsync("TSH");
        var s = await CollectAndAcceptAsync(orderId);
        var msg = E411Simulator.ResultMessage(s.Barcode, [new SimResult("10", "4.0", "mIU/L")], new DateTime(2026, 11, 2, 9, 0, 0));
        await SendAsync(msg);
        var first = await WaitForResultAsync(s.Barcode, "TSH");
        await SendAsync(msg); // same content again (lost ACK at the analyzer)
        await LimsFixture.WaitFor<bool?>(async () =>
        {
            await using var c = await f.OpenAsync();
            return await c.ExecuteScalarAsync<int>("SELECT count(*) FROM messaging.inbox_message WHERE status='duplicate' AND raw=@raw",
                new { raw = Encoding.ASCII.GetBytes(msg) }) == 1;
        }, what: "duplicate detection");

        // Rerun: different run time and status C
        await SendAsync(E411Simulator.ResultMessage(s.Barcode, [new SimResult("10", "4.1", "mIU/L", Status: "C")], new DateTime(2026, 11, 2, 9, 30, 0)));
        var second = await LimsFixture.WaitFor<Guid?>(async () =>
        {
            var latest = await WaitForResultAsync(s.Barcode, "TSH");
            return latest != first ? latest : null;
        }, what: "rerun version");
        await using var conn = await f.OpenAsync();
        var versions = (await conn.QueryAsync<(int Version, string Status, string Value)>("""
            SELECT version, status, value_text FROM clinical.result WHERE result_group=(SELECT result_group FROM clinical.result WHERE id=@first) ORDER BY version
            """, new { first })).ToList();
        Assert.Equal([(1, "superseded", "4.0"), (2, "received", "4.1")], versions);
    }

    [Fact]
    public async Task Unknown_barcode_unmapped_code_and_qc_never_reach_patient_results()
    {
        var (orderId, _, _) = await NewOrderAsync("TSH");
        var s = await CollectAndAcceptAsync(orderId);

        await SendResultAsync("999999999999", new SimResult("10", "1.0", "mIU/L"));       // unknown barcode
        await SendResultAsync(s.Barcode, new SimResult("777", "5", "U/L"));               // unmapped code
        await SendResultAsync(s.Barcode, new SimResult("20", "15", "pmol/L"));            // mapped but not ordered
        await SendAsync(E411Simulator.QcMessage("PCU1", "LOT42", "10", "1.45", "mIU/L", DateTime.Now));
        await SendAsync(E411Simulator.QcMessage(s.Barcode, "LOT42", "10", "1.45", "mIU/L", DateTime.Now)); // QC with patient barcode

        await using var c = await f.OpenAsync();
        var exceptions = await LimsFixture.WaitFor(async () =>
        {
            var rows = (await c.QueryAsync<(string Disposition, string Reason)>("""
                SELECT disposition, quarantine_reason FROM clinical.instrument_result
                WHERE (sample_id=@b OR sample_id='999999999999') AND disposition IN ('unmatched','quarantined') AND resolved_at IS NULL
                """, new { b = s.Barcode })).ToList();
            return rows.Count >= 4 ? rows : null;
        }, what: "exceptions");
        Assert.Contains(exceptions, e => e.Disposition == "unmatched");
        Assert.Contains(exceptions, e => e.Reason == "unmapped code");
        Assert.Contains(exceptions, e => e.Reason == "test not ordered on this specimen");
        Assert.Contains(exceptions, e => e.Reason == "QC record with a patient barcode");
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM clinical.qc_result WHERE control_name='PCU1'"));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM clinical.result r JOIN clinical.specimen_test st ON st.id=r.specimen_test_id
            JOIN clinical.specimen sp ON sp.id=st.specimen_id WHERE sp.barcode=@b
            """, new { b = s.Barcode }));
    }

    [Fact]
    public async Task Critical_result_needs_documented_notification()
    {
        var (orderId, _, _) = await NewOrderAsync("TSH");
        var s = await CollectAndAcceptAsync(orderId);
        await SendResultAsync(s.Barcode, new SimResult("10", ">100", "mIU/L", "H"));
        var id = await WaitForResultAsync(s.Barcode, "TSH");
        var review = f.Get<ResultReviewService>();
        await Assert.ThrowsAsync<WorkflowException>(() => review.ApproveAsync(id, "approver1", null, default));
        await review.ApproveAsync(id, "approver1", new CriticalNotification("Dr Ward 3", "phone", DateTimeOffset.UtcNow, true), default);
        await WaitForDeliveryAsync(id);
    }

    [Fact]
    public async Task Correction_after_release_goes_as_amendment_of_original()
    {
        var (orderId, hmisOrderId, _) = await NewOrderAsync("TSH");
        var s = await CollectAndAcceptAsync(orderId);
        await SendResultAsync(s.Barcode, new SimResult("10", "6.0", "mIU/L", "H"));
        var v1 = await WaitForResultAsync(s.Barcode, "TSH");
        var review = f.Get<ResultReviewService>();
        await review.ApproveAsync(v1, "approver1", null, default);
        var (_, originalRef) = await WaitForDeliveryAsync(v1);

        var v2 = await review.CorrectAsync(v1, "approver1", new CorrectionRequest("5.8", 5.8m, null, "transcription check against rerun"), default);
        await review.ApproveAsync(v2, "approver2", null, default);
        await WaitForDeliveryAsync(v2);

        var stored = f.HmisState.AllResults().Where(r => r.Item.OrderId == hmisOrderId).ToList();
        Assert.Equal(2, stored.Count);
        var amendment = stored.Single(r => r.AmendsHmisResultId is not null);
        Assert.Equal(originalRef, amendment.AmendsHmisResultId);
        Assert.Equal("5.8", amendment.Item.Value);
        Assert.Equal("amended", amendment.Item.Status);
        Assert.Equal(2, amendment.Item.Version);
    }

    [Fact]
    public async Task Database_refuses_outbox_row_without_approval()
    {
        await using var c = await f.OpenAsync();
        var ex = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => c.ExecuteAsync("""
            INSERT INTO messaging.outbox_message (id, destination, kind, aggregate_id, payload, idempotency_key, correlation_id)
            VALUES (gen_random_uuid(), 'hmis', 'hmis.result', gen_random_uuid(), '{}', 'k-' || gen_random_uuid(), 'c')
            """));
        Assert.Contains("has no approval", ex.MessageText);
    }

    [Fact]
    public async Task Audit_chain_is_append_only_and_detects_tampering()
    {
        await using var c = await f.OpenAsync();
        await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => c.ExecuteAsync("UPDATE audit.audit_event SET actor='x' WHERE seq=1"));
        await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => c.ExecuteAsync("DELETE FROM audit.audit_event WHERE seq=1"));
        Assert.True((await f.Get<AuditVerifier>().VerifyAsync(default)).Valid);

        // A superuser can bypass the trigger; verification must still catch it (AC10).
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("ALTER TABLE audit.audit_event DISABLE TRIGGER trg_audit_append_only", transaction: tx);
        await c.ExecuteAsync("UPDATE audit.audit_event SET reason='edited' WHERE seq=2", transaction: tx);
        var verifier = new AuditVerifier(new Db(new Npgsql.NpgsqlDataSourceBuilder(f.ConnectionString).Build()));
        // Verify inside the same uncommitted change by reading through this connection's view:
        var rows = await c.QueryAsync<AuditRow>("""
            SELECT seq, occurred_at AS OccurredAt, actor, actor_address AS ActorAddress, action, entity_type AS EntityType, entity_id AS EntityId,
                   accession, patient_id AS PatientId, correlation_id AS CorrelationId, reason, before_json::text AS BeforeJson,
                   after_json::text AS AfterJson, prev_hash AS PrevHash, hash FROM audit.audit_event WHERE seq=2
            """, transaction: tx);
        var row = rows.Single();
        Assert.NotEqual(row.Hash, AuditWriter.ComputeHash(row));
        await tx.RollbackAsync();
        Assert.True((await verifier.VerifyAsync(default)).Valid);
    }

    [Fact]
    public async Task Cancelled_order_line_is_not_offered_to_the_analyzer()
    {
        var (orderId, hmisOrderId, mrn) = await NewOrderAsync("TSH", "FT4");
        var s = await CollectAndAcceptAsync(orderId);
        var order = f.HmisState.GetOrder(hmisOrderId)!;
        f.HmisState.UpsertOrder(order with { Lines = [order.Lines[0], order.Lines[1] with { Status = "cancelled" }] });
        await LimsFixture.WaitFor<bool?>(async () =>
        {
            await using var c = await f.OpenAsync();
            return await c.ExecuteScalarAsync<string>("SELECT status FROM clinical.order_test WHERE order_id=@orderId AND catalog_code='FT4'", new { orderId }) == "cancelled";
        }, what: "cancellation");
        await SendAsync(E411Simulator.QueryMessage(s.Barcode));
        var reply = await f.Analyzer.ReceiveAsync();
        Assert.Contains("|^^^10^|", reply);
    }

    [Fact]
    public async Task Webhook_requires_valid_signature()
    {
        using var http = new HttpClient { BaseAddress = new Uri(f.AppUrl) };
        var body = Encoding.UTF8.GetBytes(Json.Serialize(new HmisOrderEvent("order.created", "ORD-NOPE", DateTimeOffset.UtcNow)));
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        using var bad = new HttpRequestMessage(HttpMethod.Post, "/api/hmis/v1/events") { Content = new ByteArrayContent(body) };
        bad.Headers.Add("X-Timestamp", ts);
        bad.Headers.Add("X-Signature", "sha256=00");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(bad)).StatusCode);

        using var good = new HttpRequestMessage(HttpMethod.Post, "/api/hmis/v1/events") { Content = new ByteArrayContent(body) };
        good.Headers.Add("X-Timestamp", ts);
        good.Headers.Add("X-Signature", WebhookSignature.Sign("sim-webhook-secret", ts, body));
        Assert.Equal(HttpStatusCode.Accepted, (await http.SendAsync(good)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/results/queue")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Local_accounts_lock_out_and_require_mfa_for_approvers()
    {
        var users = f.Get<UserStore>();
        await users.CreateAsync("appr-mfa", "Approver One", "correct horse battery", [Roles.Approver], "admin", default);
        var first = await users.SignInAsync("appr-mfa", "correct horse battery", null, "127.0.0.1", default);
        Assert.Equal(LoginStatus.MfaEnrollmentRequired, first.Status);
        var code = Totp.Compute(Totp.Base32Decode(first.NewTotpSecret!), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        Assert.Equal(LoginStatus.Ok, (await users.SignInAsync("appr-mfa", "correct horse battery", code, "127.0.0.1", default)).Status);
        Assert.Equal(LoginStatus.MfaRequired, (await users.SignInAsync("appr-mfa", "correct horse battery", null, "127.0.0.1", default)).Status);

        await users.CreateAsync("tech-lock", "Tech", "another long password", [Roles.Technologist], "admin", default);
        for (var i = 0; i < 5; i++) await users.SignInAsync("tech-lock", "wrong", null, "127.0.0.1", default);
        Assert.Equal(LoginStatus.LockedOut, (await users.SignInAsync("tech-lock", "another long password", null, "127.0.0.1", default)).Status);
        await Assert.ThrowsAsync<WorkflowException>(() => users.CreateAsync("short", "S", "short", [Roles.Technologist], "admin", default));
    }
}
