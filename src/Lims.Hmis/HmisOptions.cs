namespace Lims.Hmis;

/// <summary>Values from design 1.1 (H4, H5, H6). All PROPOSED until the HMIS team agrees the contract.</summary>
public sealed class HmisOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5080/api/lab/v1/";
    public string TokenUrl { get; set; } = "http://localhost:5080/oauth/token";
    public string ClientId { get; set; } = "";
    /// <summary>Never in plain appsettings in production: supplied by DPAPI-protected secret store (design 9.4).</summary>
    public string ClientSecret { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public TimeSpan WebhookMaxSkew { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);
    public bool PollingEnabled { get; set; } = true;
    public int PageSize { get; set; } = 100;
    public int RequestsPerMinute { get; set; } = 60;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool SupportsReadBack { get; set; } = true;
    public int MaxAttempts { get; set; } = 50;
    public TimeSpan MaxRetryAge { get; set; } = TimeSpan.FromHours(24);
    public int CircuitBreakerFailures { get; set; } = 5;
    public TimeSpan CircuitBreakerPause { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan DispatchIdleDelay { get; set; } = TimeSpan.FromSeconds(2);
}
