using Dapper;
using Lims.Clinical;
using Lims.Contracts;
using Lims.Host;
using Lims.Host.Operations;
using Lims.Platform;
using Lims.Sim.E411;

namespace Lims.IntegrationTests;

/// <summary>Go-live operations: reconciliation and critical escalation, storage gate, catalog sync, exception resolve, CLI.</summary>
public sealed partial class EndToEndTests
{
    [Fact]
    public async Task Unacknowledged_critical_result_is_escalated_once_and_closes_when_approved()
    {
        var (orderId, _, _) = await NewOrderAsync("TSH");
        var s = await CollectAndAcceptAsync(orderId);
        await SendResultAsync(s.Barcode, new SimResult("10", ">100", "mIU/L", "H"));
        var id = await WaitForResultAsync(s.Barcode, "TSH");
        await Task.Delay(1500); // fixture escalates criticals after 1 second

        var recon = f.Get<ReconciliationService>();
        await recon.RunOnceAsync(default);
        await recon.RunOnceAsync(default);

        await using var c = await f.OpenAsync();
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM platform.reconciliation_item WHERE kind='critical_unacknowledged' AND entity_id=@e AND resolved_at IS NULL", new { e = id.ToString() }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM audit.audit_event WHERE action='result.critical_escalated' AND entity_id=@e", new { e = id.ToString() }));

        await f.Get<ResultReviewService>().ApproveAsync(id, "approver1", new CriticalNotification("Dr Ward 3", "phone", DateTimeOffset.UtcNow, true), default);
        await WaitForDeliveryAsync(id);
        await recon.RunOnceAsync(default);
        Assert.Equal("condition cleared", await c.ExecuteScalarAsync<string>(
            "SELECT resolution FROM platform.reconciliation_item WHERE kind='critical_unacknowledged' AND entity_id=@e", new { e = id.ToString() }));
    }

    [Fact]
    public async Task Low_disk_makes_the_link_refuse_new_messages_until_space_returns()
    {
        var housekeeping = f.Get<HousekeepingService>();
        var gate = f.Get<StorageGate>();
        try
        {
            await housekeeping.CheckAsync(3.0, default);
            Assert.Equal(StorageLevel.Refusing, gate.Level);
            Assert.Equal(SendStatus.Refused, await f.Analyzer.SendAsync(E411Simulator.ResultMessage("000000000000", [new SimResult("10", "1.0", "mIU/L", "N")], DateTime.Now)));
        }
        finally
        {
            await housekeeping.CheckAsync(50.0, default);
        }
        Assert.Equal(StorageLevel.Ok, gate.Level);

        var (orderId, _, _) = await NewOrderAsync("TSH");
        var s = await CollectAndAcceptAsync(orderId);
        await SendResultAsync(s.Barcode, new SimResult("10", "1.5", "mIU/L", "N"));
        await WaitForResultAsync(s.Barcode, "TSH");

        await using var c = await f.OpenAsync();
        Assert.True(await c.ExecuteScalarAsync<int>("SELECT count(*) FROM audit.audit_event WHERE action='storage.level_changed'") >= 2);
    }

    [Fact]
    public async Task Hmis_catalog_change_waits_for_lab_manager_approval()
    {
        var code = $"CRP{Random.Shared.Next(1000, 9999)}";
        f.HmisState.Catalog.Enqueue(new HmisCatalogEntry(code, "C-reactive protein", "1988-5", "mg/L"));
        var sync = f.Get<CatalogSyncService>();

        Assert.Equal(1, await sync.SyncOnceAsync(default));
        Assert.Equal(0, await sync.SyncOnceAsync(default)); // unchanged entries are not proposed again

        await using var c = await f.OpenAsync();
        Assert.Equal("proposed", await c.ExecuteScalarAsync<string>("SELECT status FROM clinical.test_catalog WHERE code=@code", new { code }));

        var catalog = f.Get<CatalogService>();
        await Assert.ThrowsAsync<WorkflowException>(() => catalog.ApproveAsync(code, "system:catalog-sync", default));
        await catalog.ApproveAsync(code, "labmgr1", default);
        Assert.Equal("active", await c.ExecuteScalarAsync<string>("SELECT status FROM clinical.test_catalog WHERE code=@code", new { code }));
        Assert.Equal(1, await c.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM audit.audit_event WHERE action='catalog.approved' AND entity_id LIKE @p", new { p = code + ":%" }));
    }

    [Fact]
    public async Task Unmatched_result_exception_is_resolved_with_a_reason()
    {
        var barcode = $"9{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";
        await SendResultAsync(barcode, new SimResult("10", "2.0", "mIU/L", "N"));
        await using var c = await f.OpenAsync();
        var id = await LimsFixture.WaitFor<Guid?>(() => c.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM clinical.instrument_result WHERE sample_id=@barcode AND disposition='unmatched'", new { barcode }), what: "unmatched result");

        var queries = f.Get<ClinicalQueries>();
        var audit = f.Get<Lims.Audit.AuditWriter>();
        await Assert.ThrowsAsync<WorkflowException>(() => queries.ResolveExceptionAsync(id!.Value, "approver1", " ", audit, default));
        Assert.Equal(1, await queries.ResolveExceptionAsync(id!.Value, "approver1", "Tube relabelled and re-run", audit, default));
        Assert.Equal(0, await queries.ResolveExceptionAsync(id!.Value, "approver1", "again", audit, default));
        Assert.Equal("approver1", await c.ExecuteScalarAsync<string>("SELECT resolved_by FROM clinical.instrument_result WHERE id=@id", new { id }));
    }

    [Fact]
    public async Task Cli_verifies_the_audit_chain_and_rejects_missing_arguments()
    {
        var output = new StringWriter();
        Assert.Equal(0, await LimsCli.RunAsync(["migrate", "--connection", f.ConnectionString], TextReader.Null, output, TextWriter.Null));
        Assert.Equal(0, await LimsCli.RunAsync(["verify-audit", "--connection", f.ConnectionString], TextReader.Null, output, TextWriter.Null));
        Assert.Contains("\"valid\":true", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await LimsCli.RunAsync(["verify-audit"], TextReader.Null, TextWriter.Null, TextWriter.Null));
    }
}
