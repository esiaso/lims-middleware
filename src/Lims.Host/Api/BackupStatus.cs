namespace Lims.Host.Api;

/// <summary>Where backup.ps1 writes its archives (its -LocalTarget). The health screen shows the newest archive's time.</summary>
public sealed class BackupStatusOptions
{
    public string? Directory { get; set; }
    /// <summary>Older than this and the tile turns red (nightly schedule plus slack).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(26);
}

public static class BackupStatus
{
    public static DateTimeOffset? LastBackupAt(BackupStatusOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Directory) || !System.IO.Directory.Exists(o.Directory)) return null;
        try
        {
            var newest = new DirectoryInfo(o.Directory).EnumerateFiles("*.7z", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            return newest is null ? null : new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
