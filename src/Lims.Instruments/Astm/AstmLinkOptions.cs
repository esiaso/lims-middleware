namespace Lims.Instruments.Astm;

/// <summary>Timers and retry limits (e411 values from manual section D; c111 differs, see design 6.3).</summary>
public sealed class AstmLinkOptions
{
    public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan NakRetryDelay { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ContentionRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public int MaxSendAttempts { get; set; } = 6;
    public TimeSpan IdlePoll { get; set; } = TimeSpan.FromMilliseconds(250);
}
