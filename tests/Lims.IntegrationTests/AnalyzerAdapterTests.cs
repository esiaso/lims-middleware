using Dapper;
using Lims.Clinical;
using Lims.Platform;
using Lims.Sim.Bc5000;
using Lims.Sim.E411;

namespace Lims.IntegrationTests;

/// <summary>cobas c 111 and Mindray BC-5000 adapters end to end, and test panels. Simulated traffic only.</summary>
public sealed partial class EndToEndTests
{
    private async Task SendC111Async(string message) => Assert.Equal(SendStatus.Accepted, await f.C111.SendAsync(message));

    [Fact]
    public async Task C111_query_result_and_qc_end_to_end()
    {
        var (orderId, hmisOrderId, _) = await NewOrderAsync("GLU");
        var s = await CollectAndAcceptAsync(orderId);

        await SendC111Async(C111Messages.Query(s.Barcode, DateTime.Now));
        var reply = await f.C111.ReceiveAsync();
        Assert.NotNull(reply);
        Assert.Contains($"O|1|{s.Barcode}||^^^989^|R||||||A", reply);
        Assert.EndsWith("O\\Q\rL|1|N\r", reply);

        await SendC111Async(C111Messages.Results(s.Barcode, [new SimResult("989", "6.1", "mmol/L", "H", AlarmCode: "40^>RR")],
            DateTime.Now.AddSeconds(Interlocked.Increment(ref _seq))));
        var glu = await WaitForResultAsync(s.Barcode, "GLU");
        await using (var c = await f.OpenAsync())
        {
            var row = await c.QuerySingleAsync("SELECT value_text, abnormal_flag, instrument_flags FROM clinical.result WHERE id=@glu", new { glu });
            Assert.Equal("6.1", (string)row.value_text);
            Assert.Equal("H", (string)row.abnormal_flag);
            Assert.Contains("alarm:40", (string[])row.instrument_flags);
        }

        await f.Get<ResultReviewService>().ApproveAsync(glu, "approver1", null, default);
        await WaitForDeliveryAsync(glu);
        Assert.Equal("GLU", f.HmisState.AllResults().Single(r => r.Item.OrderId == hmisOrderId).Item.TestCode);

        await SendC111Async(C111Messages.Qc(301, "989", "5.2", "mmol/L", DateTime.Now));
        await LimsFixture.WaitFor<bool?>(async () =>
        {
            await using var c = await f.OpenAsync();
            return await c.ExecuteScalarAsync<int>("SELECT count(*) FROM clinical.qc_result WHERE instrument_id='c111-1' AND control_name='1301'") >= 1;
        }, what: "c111 QC");
    }

    [Fact]
    public async Task C111_unknown_sample_query_gets_Z()
    {
        await SendC111Async(C111Messages.Query("000000000001", DateTime.Now));
        var reply = await f.C111.ReceiveAsync();
        Assert.Contains("|Z\r", reply);
    }

    [Fact]
    public async Task Cbc_panel_order_is_resulted_per_parameter_on_the_bc5000()
    {
        var (orderId, hmisOrderId, _) = await NewOrderAsync("CBC");
        await using (var c = await f.OpenAsync())
        {
            var lines = (await c.QueryAsync<(string Line, string Code, string Panel)>(
                "SELECT hmis_line_id, catalog_code, panel_code FROM clinical.order_test WHERE order_id=@orderId ORDER BY catalog_code", new { orderId })).ToList();
            Assert.Equal(["HGB", "NEUP", "PLT", "WBC"], lines.Select(l => l.Code));
            Assert.All(lines, l => Assert.Equal("CBC", l.Panel));
            Assert.Single(lines.Select(l => l.Line).Distinct());
        }
        var s = await CollectAndAcceptAsync(orderId); // one whole-blood tube for the whole panel

        var orr = await f.Bc5000.SendAsync(f.Bc5000.WorklistQuery(s.Barcode, DateTime.Now));
        Assert.NotNull(orr);
        Assert.Contains("ORR^O02", orr);
        Assert.Contains($"ORC|AF|{s.Barcode}", orr);
        Assert.Contains("08003^Test Mode^99MRC||CBC+DIFF|", orr); // NEU% is a differential parameter

        var parameters = Bc5000Simulator.NormalCbc().Where(p => p.Code is "6690-2" or "718-7" or "777-3" or "770-8").ToList();
        var ack = await f.Bc5000.SendAsync(f.Bc5000.ResultMessage(s.Barcode, parameters, DateTime.Now, alerts: ["12004^Lymphocytosis?"]));
        Assert.Equal("AA", Bc5000Simulator.AckCode(ack));

        var review = f.Get<ResultReviewService>();
        var ids = new List<Guid>();
        foreach (var code in new[] { "WBC", "HGB", "PLT", "NEUP" })
            ids.Add(await WaitForResultAsync(s.Barcode, code));
        await using (var c = await f.OpenAsync())
        {
            var flags = await c.ExecuteScalarAsync<string[]>("SELECT instrument_flags FROM clinical.result WHERE id=@id", new { id = ids[0] });
            Assert.Contains("alert:Lymphocytosis?", flags!);
        }
        foreach (var id in ids) await review.ApproveAsync(id, "approver1", null, default);
        foreach (var id in ids) await WaitForDeliveryAsync(id);

        var delivered = f.HmisState.AllResults().Where(r => r.Item.OrderId == hmisOrderId).Select(r => r.Item).ToList();
        Assert.Equal(4, delivered.Count);
        Assert.Single(delivered.Select(d => d.OrderLineId).Distinct()); // all on the CBC line
        Assert.Equal(["6690-2", "718-7", "770-8", "777-3"], delivered.Select(d => d.Loinc!).Order());
        Assert.Equal("138", delivered.Single(d => d.TestCode == "HGB").Value);
    }

    [Fact]
    public async Task Cancelling_a_panel_line_cancels_every_component()
    {
        var (orderId, hmisOrderId, _) = await NewOrderAsync("CBC");
        var order = f.HmisState.GetOrder(hmisOrderId)!;
        f.HmisState.UpsertOrder(order with { UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1), Lines = [order.Lines[0] with { Status = "cancelled" }] });
        await LimsFixture.WaitFor<bool?>(async () =>
        {
            await using var c = await f.OpenAsync();
            return await c.ExecuteScalarAsync<int>("SELECT count(*) FROM clinical.order_test WHERE order_id=@orderId AND status='cancelled'", new { orderId }) == 4;
        }, what: "panel cancellation");
    }

    [Fact]
    public async Task Bc5000_unknown_sample_and_unstored_message_handling()
    {
        var reply = await f.Bc5000.SendAsync(f.Bc5000.WorklistQuery("000000000002", DateTime.Now));
        Assert.Contains("MSA|AA|", reply);
        Assert.DoesNotContain("ORC|", reply);

        var ack = await f.Bc5000.SendAsync(f.Bc5000.ResultMessage("000000000002", Bc5000Simulator.NormalCbc().Take(1), DateTime.Now));
        Assert.Equal("AA", Bc5000Simulator.AckCode(ack)); // stored, then filed as an unmatched exception
        await LimsFixture.WaitFor<bool?>(async () =>
        {
            await using var c = await f.OpenAsync();
            return await c.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM clinical.instrument_result WHERE instrument_id='bc5000-1' AND sample_id='000000000002' AND disposition='unmatched'") == 1;
        }, what: "unmatched BC-5000 result");
    }

    [Fact]
    public async Task Panel_must_list_active_plain_tests()
    {
        var catalog = f.Get<CatalogService>();
        await Assert.ThrowsAsync<WorkflowException>(() => catalog.ProposeAsync(new CatalogEntry("BADP", "Bad panel", null, null, 0, null, null, null, null,
            "whole_blood", null, PanelComponents: ["WBC", "NOPE"]), "labmgr1", default));
        await Assert.ThrowsAsync<WorkflowException>(() => catalog.ProposeAsync(new CatalogEntry("BADP", "Nested panel", null, null, 0, null, null, null, null,
            "whole_blood", null, PanelComponents: ["CBC"]), "labmgr1", default));
        await Assert.ThrowsAsync<WorkflowException>(() => catalog.ProposeAsync(new CatalogEntry("BADP", "Repeat", null, null, 0, null, null, null, null,
            "whole_blood", null, PanelComponents: ["WBC", "WBC"]), "labmgr1", default));
    }
}
