using Dapper;
using Lims.Clinical;
using Lims.Platform;
using Lims.Sim.E411;

namespace Lims.IntegrationTests;

public sealed partial class EndToEndTests
{
    [Fact]
    public async Task Catalog_edit_with_delta_limit_flags_a_jump_and_approval_needs_confirmation()
    {
        var catalog = f.Get<CatalogService>();
        var tsh = new CatalogEntry("TSH", "Thyroid stimulating hormone", "3016-3", "mIU/L", 3, 0.27m, 4.2m, null, 50m, "serum", "e411-1",
            DeltaPercent: 50m, DeltaWindowDays: 30);
        await catalog.ProposeAsync(tsh, "labmgr1", default);
        await Assert.ThrowsAsync<WorkflowException>(() => catalog.ApproveAsync("TSH", "labmgr1", default));
        await catalog.ApproveAsync("TSH", "labmgr2", default);

        var review = f.Get<ResultReviewService>();
        var mrn = $"MRN-DELTA-{Random.Shared.Next(100000, 999999)}";

        async Task<Guid> RunAsync(string value)
        {
            var (orderId, _, _) = await NewOrderForAsync(mrn, "TSH");
            var s = await CollectAndAcceptAsync(orderId);
            await SendResultAsync(s.Barcode, new SimResult("10", value, "mIU/L", "N"));
            return await WaitForResultAsync(s.Barcode, "TSH");
        }

        var first = await RunAsync("2.000");
        await review.ApproveAsync(first, "approver1", null, default);

        var jump = await RunAsync("3.500"); // +75%, limit 50%
        await using var c = await f.OpenAsync();
        var row = await c.QuerySingleAsync<(bool Flag, decimal? Prev, Guid? PrevId)>(
            "SELECT delta_flag, delta_previous_value, delta_previous_id FROM clinical.result WHERE id=@jump", new { jump });
        Assert.True(row.Flag);
        Assert.Equal(2.000m, row.Prev);
        Assert.Equal(first, row.PrevId);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT count(*) FROM audit.audit_event WHERE action='result.delta_flagged' AND entity_id=@e", new { e = jump.ToString() }));

        await Assert.ThrowsAsync<WorkflowException>(() => review.ApproveAsync(jump, "approver1", null, default));
        await review.ApproveAsync(jump, "approver1", null, default, deltaComment: "Dose changed last week, confirmed with ward");
        await WaitForDeliveryAsync(jump);

        var small = await RunAsync("3.900"); // +11% from the latest released value
        Assert.False(await c.ExecuteScalarAsync<bool>("SELECT delta_flag FROM clinical.result WHERE id=@small", new { small }));
        await review.ApproveAsync(small, "approver1", null, default);
    }
}
