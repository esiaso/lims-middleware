using Lims.Clinical;
using Lims.Hmis;
using Microsoft.Extensions.Options;

namespace Lims.Host.Operations;

public sealed class CatalogSyncOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>Daily import of the HMIS test catalog (GET /test-catalog, decision H9) into the catalog approval queue.</summary>
public sealed class CatalogSyncService(HmisClient client, CatalogService catalog, IOptions<CatalogSyncOptions> options, ILogger<CatalogSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var n = await SyncOnceAsync(stoppingToken);
                if (n > 0) logger.LogInformation("Catalog sync proposed {Count} changes for Lab Manager approval", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Catalog sync failed: {Error}", ex.Message); }
            await Task.Delay(options.Value.Interval, stoppingToken);
        }
    }

    public async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        var entries = await client.GetCatalogAsync(ct);
        var proposed = 0;
        foreach (var e in entries)
            if (await catalog.ProposeFromHmisAsync(e.TestCode, e.Name, e.Loinc, e.Unit, ct)) proposed++;
        return proposed;
    }
}
