namespace Lims.Platform;

public enum StorageLevel { Ok, Warning, Critical, Refusing }

/// <summary>
/// Shared by housekeeping and the analyzer links: below the refusal threshold the links answer NAK rather
/// than acknowledge something the server may not be able to store (design 7.6).
/// </summary>
public sealed class StorageGate
{
    public StorageLevel Level { get; set; } = StorageLevel.Ok;
    public double? FreePercent { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public bool AcceptingNewMessages => Level != StorageLevel.Refusing;

    public static StorageLevel Classify(double freePercent, double warning = 20, double critical = 10, double refuse = 5) =>
        freePercent < refuse ? StorageLevel.Refusing
        : freePercent < critical ? StorageLevel.Critical
        : freePercent < warning ? StorageLevel.Warning
        : StorageLevel.Ok;
}
