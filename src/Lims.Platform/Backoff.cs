namespace Lims.Platform;

/// <summary>Retry schedule from design 7.4: 10 s, 30 s, 1 min, 2 min, 5 min, then every 15 min, with jitter.</summary>
public static class Backoff
{
    private static readonly TimeSpan[] Steps =
    [
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
    ];

    public static TimeSpan Delay(int attempt, Random? rng = null)
    {
        var baseDelay = attempt <= 0 ? Steps[0] : attempt <= Steps.Length ? Steps[attempt - 1] : TimeSpan.FromMinutes(15);
        var jitter = (rng ?? Random.Shared).NextDouble() * 0.2 - 0.1; // +/-10%
        return baseDelay + TimeSpan.FromTicks((long)(baseDelay.Ticks * jitter));
    }
}
