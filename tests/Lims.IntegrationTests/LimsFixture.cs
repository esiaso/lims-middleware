using System.Net;
using System.Net.Sockets;
using Dapper;
using Lims.Clinical;
using Lims.Host;
using Lims.Instruments;
using Lims.Platform;
using Lims.Sim.Bc5000;
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
    /// <summary>cobas c 111 on its own serial device server (same ASTM link layer as the e411 simulator).</summary>
    public E411Simulator C111 { get; private set; } = null!;
    /// <summary>Mindray BC-5000: the analyzer connects to the middleware (TcpServer mode).</summary>
    public Bc5000Simulator Bc5000 { get; private set; } = null!;
    private TcpListener _deviceServer = null!;
    private TcpListener _c111DeviceServer = null!;
    private TcpClient? _analyzerConnection;
    private TcpClient? _c111Connection;
    private TcpClient? _bc5000Connection;

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
        _c111DeviceServer = new TcpListener(IPAddress.Loopback, 0);
        _c111DeviceServer.Start();
        var c111Port = ((IPEndPoint)_c111DeviceServer.LocalEndpoint).Port;
        var acceptC111 = _c111DeviceServer.AcceptTcpClientAsync();
        var bc5000Port = FreePort();

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
                ["Instruments:Instruments:1:Id"] = "c111-1",
                ["Instruments:Instruments:1:Model"] = "cobas c 111",
                ["Instruments:Instruments:1:Parser"] = "roche.cobas-c111.astm",
                ["Instruments:Instruments:1:HostQuery"] = "true",
                ["Instruments:Instruments:1:Transport:Mode"] = "TcpClient",
                ["Instruments:Instruments:1:Transport:Host"] = "127.0.0.1",
                ["Instruments:Instruments:1:Transport:Port"] = c111Port.ToString(),
                ["Instruments:Instruments:2:Id"] = "bc5000-1",
                ["Instruments:Instruments:2:Model"] = "Mindray BC-5000",
                ["Instruments:Instruments:2:Parser"] = "mindray.bc5000.hl7",
                ["Instruments:Instruments:2:HostQuery"] = "true",
                ["Instruments:Instruments:2:Transport:Mode"] = "TcpServer",
                ["Instruments:Instruments:2:Transport:Host"] = "127.0.0.1",
                ["Instruments:Instruments:2:Transport:Port"] = bc5000Port.ToString(),
                ["AuditExport:FileSinkDirectory"] = Path.Combine(Path.GetTempPath(), DatabaseName),
                ["CatalogSync:Enabled"] = "false",
                ["Reconciliation:Interval"] = "01:00:00",
                ["Reconciliation:CriticalEscalation"] = "00:00:01",
                ["Housekeeping:Interval"] = "01:00:00",
                ["Bootstrap:AdminUsername"] = "admin",
                ["Bootstrap:AdminPassword"] = "bootstrap-password-123",
            });
        });
        await LimsApp.InitializeAsync(App);
        await App.StartAsync();
        AppUrl = App.Urls.First();

        _analyzerConnection = await accept.WaitAsync(TimeSpan.FromSeconds(30));
        Analyzer = new E411Simulator(_analyzerConnection.GetStream(), TimeSpan.FromSeconds(20));
        _c111Connection = await acceptC111.WaitAsync(TimeSpan.FromSeconds(30));
        C111 = new E411Simulator(_c111Connection.GetStream(), TimeSpan.FromSeconds(20));
        _bc5000Connection = await ConnectWithRetryAsync(bc5000Port);
        Bc5000 = new Bc5000Simulator(_bc5000Connection.GetStream(), TimeSpan.FromSeconds(20));

        await SeedAsync();
    }

    private async Task SeedAsync()
    {
        var catalog = Get<CatalogService>();
        await catalog.PublishAsync(new CatalogEntry("TSH", "Thyroid stimulating hormone", "3016-3", "mIU/L", 3, 0.27m, 4.2m, null, 50m, "serum", "e411-1"), "labmgr1", default);
        await catalog.PublishAsync(new CatalogEntry("FT4", "Free thyroxine", "3024-7", "pmol/L", 2, 12m, 22m, 5m, 60m, "serum", "e411-1"), "labmgr1", default);
        await catalog.PublishAsync(new CatalogEntry("GLU", "Glucose", "2345-7", "mmol/L", 1, 3.9m, 5.6m, 2.2m, 25m, "serum", "c111-1"), "labmgr1", default);
        await catalog.PublishAsync(new CatalogEntry("WBC", "Leukocytes", "6690-2", "10*9/L", 2, 4m, 10m, 1m, 30m, "whole_blood", "bc5000-1"), "labmgr1", default);
        await catalog.PublishAsync(new CatalogEntry("HGB", "Hemoglobin", "718-7", "g/L", 0, 110m, 160m, 50m, 200m, "whole_blood", "bc5000-1"), "labmgr1", default);
        await catalog.PublishAsync(new CatalogEntry("PLT", "Platelets", "777-3", "10*9/L", 0, 100m, 300m, 20m, 1000m, "whole_blood", "bc5000-1"), "labmgr1", default);
        await catalog.PublishAsync(new CatalogEntry("NEUP", "Neutrophils/100 leukocytes", "770-8", "%", 1, 50m, 70m, null, null, "whole_blood", "bc5000-1"), "labmgr1", default);
        await catalog.PublishAsync(new CatalogEntry("CBC", "Complete blood count", null, null, 0, null, null, null, null, "whole_blood", "bc5000-1",
            PanelComponents: ["WBC", "HGB", "PLT", "NEUP"]), "labmgr1", default);
        await catalog.PublishAsync(new CatalogEntry("HBSAG", "Hepatitis B surface antigen", "5196-1", null, 3, null, null, null, null, "serum", "e411-1"), "labmgr1", default);
        var mappings = Get<MappingService>();
        foreach (var (code, cat, type) in new[] { ("10", "TSH", "numeric"), ("20", "FT4", "numeric"), ("400", "HBSAG", "qualitative") })
        {
            var id = await mappings.ProposeAsync(new MappingProposal("e411-1", code, cat, type, 1m, null, true), "labmgr1", default);
            await mappings.ApproveAsync(id, "labmgr2", default);
        }
        foreach (var (inst, code, cat) in new[] { ("c111-1", "989", "GLU"), ("bc5000-1", "6690-2", "WBC"), ("bc5000-1", "718-7", "HGB"),
                     ("bc5000-1", "777-3", "PLT"), ("bc5000-1", "770-8", "NEUP") })
        {
            var id = await mappings.ProposeAsync(new MappingProposal(inst, code, cat, "numeric", 1m, null, true), "labmgr1", default);
            await mappings.ApproveAsync(id, "labmgr2", default);
        }
    }

    public async Task DisposeAsync()
    {
        try { await App.StopAsync(); } catch { /* best effort */ }
        try { await Hmis.StopAsync(); } catch { /* best effort */ }
        _analyzerConnection?.Dispose();
        _c111Connection?.Dispose();
        _bc5000Connection?.Dispose();
        _deviceServer.Stop();
        _c111DeviceServer.Stop();
        await App.DisposeAsync();
        await Hmis.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        if (Environment.GetEnvironmentVariable("LIMS_KEEP_DB") == "1") return;
        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<TcpClient> ConnectWithRetryAsync(int port)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var c = new TcpClient();
            try { await c.ConnectAsync(IPAddress.Loopback, port); return c; }
            catch (SocketException) when (DateTime.UtcNow < until) { c.Dispose(); await Task.Delay(200); }
        }
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
