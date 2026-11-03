using Lims.Audit;
using Lims.Platform;
using Microsoft.Extensions.Options;

namespace Lims.Host.Operations;

public sealed class HousekeepingOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Folder on the data volume (PostgreSQL data and raw messages). Default: the install folder's volume.</summary>
    public string? DataPath { get; set; }
    public double WarningPercent { get; set; } = 20;
    public double CriticalPercent { get; set; } = 10;
    public double RefusePercent { get; set; } = 5;
}

/// <summary>Disk space watch (design 7.6): warn at 20%, critical at 10%, stop accepting analyzer messages at 5%.</summary>
public sealed class HousekeepingService(StorageGate gate, Db db, AuditWriter audit, IOptions<HousekeepingOptions> options, ILogger<HousekeepingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAsync(FreePercent(), stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Housekeeping check failed"); }
            await Task.Delay(options.Value.Interval, stoppingToken);
        }
    }

    private double FreePercent()
    {
        var path = options.Value.DataPath ?? AppContext.BaseDirectory;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        return 100.0 * drive.AvailableFreeSpace / drive.TotalSize;
    }

    public async Task CheckAsync(double freePercent, CancellationToken ct)
    {
        var o = options.Value;
        var level = StorageGate.Classify(freePercent, o.WarningPercent, o.CriticalPercent, o.RefusePercent);
        var previous = gate.Level;
        gate.Level = level;
        gate.FreePercent = Math.Round(freePercent, 1);
        gate.CheckedAt = DateTimeOffset.UtcNow;
        if (level == previous) return;
        var log = level switch { StorageLevel.Ok => LogLevel.Information, StorageLevel.Warning => LogLevel.Warning, _ => LogLevel.Critical };
        logger.Log(log, "Data volume {Free:F1}% free: storage level {Previous} -> {Level}", freePercent, previous, level);
        try
        {
            await audit.WriteAsync(db, new AuditEntry("system:housekeeping", "storage.level_changed", "server", Environment.MachineName,
                Before: new { level = previous.ToString() }, After: new { level = level.ToString(), freePercent = gate.FreePercent }), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not audit storage level change"); }
    }
}
