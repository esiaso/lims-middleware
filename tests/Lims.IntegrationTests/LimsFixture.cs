using System.Net;
using System.Net.Sockets;
using Dapper;
using Lims.Clinical;
using Lims.Host;
using Lims.Instruments;
using Lims.Platform;
using Lims.Sim.E411;
using Lims.Sim.Hmis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Lims.IntegrationTests;

/// <summary>
/// Real PostgreSQL + real HMIS simulator (Kestrel) + middleware host + e411 simulator over TCP.
/// Needs PostgreSQL: set LIMS_TEST_PG (default Host=localhost;Username=lims_test;Password=lims_test).
/// Each run creates and drops its own database.
/// </summary>
public sealed class LimsFixture : IAsyncLifetime
{
    public static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("LIMS_TEST_PG") ?? "Host=localhost;Username=lims_test;Password=lims_test;Database=postgres";

    public string DatabaseName { get; } = $"lims_it_{Guid.NewGuid():N}";
    public string ConnectionString { get; private set; } = "";
    public WebApplication Hmis { get; private set; } = null!;
    public HmisSimulatorState HmisState => Hmis.Services.GetRequiredService<HmisSimulatorState>();
    public WebApplication App { get; private set; } = null!;
    public string AppUrl { get; private set; } = "";
    public E411Simulator Analyzer { get; private set; } = null!;
    private TcpListener _deviceServer = null!;
    private TcpClient? _analyzerConnection;

    public T Get<T>() where T : notnull => App.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(AdminConnectionString))
            await admin.ExecuteAsync($"CREATE DATABASE {DatabaseName}");
        ConnectionString = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = DatabaseName }.ConnectionString;

        Hmis = HmisSimulatorProgram.BuildApp(["--urls", "http://127.0.0.1:0", "--environment", "Testing"],
            b => b.Logging.SetMinimumLevel(LogLevel.Warning));
        await Hmis.StartAsync();
        var hmisUrl = Hmis.Urls.First();

        // The middleware connects to the "serial device server", played here by a TCP listener.
        _deviceServer = new TcpListener(IPAddress.Loopback, 0);
        _deviceServer.Start();
        var analyzerPort = ((IPEndPoint)_deviceServer.LocalEndpoint).Port;
        var accept = _deviceServer.AcceptTcpClientAsync();

        App = LimsApp.Build(["--environment", "Testing"], b =>
        {
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Logging.SetMinimumLevel(LogLevel.Warning);
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Lims"] = ConnectionString,
                ["Hmis:BaseUrl"] = $"{hmisUrl}/api/lab/v1/",
                ["Hmis:TokenUrl"] = $"{hmisUrl}/oauth/token",
                ["Hmis:ClientId"] = "lims-middleware",
                ["Hmis:ClientSecret"] = "sim-secret",
                ["Hmis:WebhookSecret"] = "sim-webhook-secret",
                ["Hmis:PollInterval"] = "00:00:00.300",
                ["Hmis:DispatchIdleDelay"] = "00:00:00.200",
                ["Hmis:CircuitBreakerFailures"] = "50",
                ["Inbox:IdleDelay"] = "00:00:00.100",
                ["Instruments:Instruments:0:Id"] = "e411-1",
                ["Instruments:Instruments:0:Parser"] = "roche.cobas-e411.astm",
                ["Instruments:Instruments:0:Variant"] = "Cobas",
                ["Instruments:Instruments:0:Enabled"] = "true",
                ["Instruments:Instruments:0:HostQuery"] = "true",
                ["Instruments:Instruments:0:Transport:Mode"] = "TcpClient",
                ["Instruments:Instruments:0:Transport:Host"] = "127.0.0.1",
                ["Instruments:Instruments:0:Transport:Port"] = analyzerPort.ToString(),
                ["AuditExport:FileSinkDirectory"] = Path.Combine(Path.GetTempPath(), DatabaseName),
                ["Bootstrap:AdminUsername"] = "admin",
                ["Bootstrap:AdminPassword"] = "bootstrap-password-123",
            });
        });
        await LimsApp.InitializeAsync(App);
        await App.StartAsync();
        AppUrl = App.Urls.First();

        _analyzerConnection = await accept.WaitAsync(TimeSpan.FromSeconds(30));
        Analyzer = new E411Simulator(_analyzerConnection.GetStream(), TimeSpan.FromSeconds(20));

        await SeedAsync();
    }

    private async Task SeedAsync()
    {
        var catalog = Get<CatalogService>();
        await catalog.PublishAsync(new CatalogEntry("TSH", "Thyroid stimulating hormone", "3016-3", "mIU/L", 3, 0.27m, 4.2m, null, 50m, "serum", "e411-1"), "labmgr1", default);
        await catalog.PublishAsync(new CatalogEntry("FT4", "Free thyroxine", "3024-7", "pmol/L", 2, 12m, 22m, 5m, 60m, "serum", "e411-1"), "labmgr1", default);
        await catalog.PublishAsync(new CatalogEntry("HBSAG", "Hepatitis B surface antigen", "5196-1", null, 3, null, null, null, null, "serum", "e411-1"), "labmgr1", default);
        var mappings = Get<MappingService>();
        foreach (var (code, cat, type) in new[] { ("10", "TSH", "numeric"), ("20", "FT4", "numeric"), ("400", "HBSAG", "qualitative") })
        {
            var id = await mappings.ProposeAsync(new MappingProposal("e411-1", code, cat, type, 1m, null, true), "labmgr1", default);
            await mappings.ApproveAsync(id, "labmgr2", default);
        }
    }

    public async Task DisposeAsync()
    {
        try { await App.StopAsync(); } catch { /* best effort */ }
        try { await Hmis.StopAsync(); } catch { /* best effort */ }
        _analyzerConnection?.Dispose();
        _deviceServer.Stop();
        await App.DisposeAsync();
        await Hmis.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        if (Environment.GetEnvironmentVariable("LIMS_KEEP_DB") == "1") return;
        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)");
    }

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync();
        return c;
    }

    public static async Task<T> WaitFor<T>(Func<Task<T?>> probe, TimeSpan? timeout = null, string? what = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < until)
        {
            var v = await probe();
            if (v is not null && !(v is bool b && !b)) return v;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out waiting for {what ?? "condition"}");
    }
}

[CollectionDefinition("lims")]
public sealed class LimsCollection : ICollectionFixture<LimsFixture>;
