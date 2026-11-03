using System.Collections.Concurrent;
using Dapper;
using Lims.Instruments.Astm;
using Lims.Instruments.Registration;
using Lims.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lims.Instruments;

public sealed class LinkHealthRegistry
{
    private readonly ConcurrentDictionary<string, LinkHealth> _links = new();
    public LinkHealth Get(string id) => _links.GetOrAdd(id, i => new LinkHealth { InstrumentId = i });
    public IReadOnlyCollection<LinkHealth> All => _links.Values.ToList();
}

/// <summary>
/// One link loop per enabled instrument: open transport, run the ASTM or HL7 session, reconnect with backoff 1 s to 60 s.
/// Also supervises analyzers registered from the screens: an approved one starts without a service restart, a new
/// approved version restarts its link, and a disabled one stops.
/// </summary>
public sealed class InstrumentLinkWorker(
    IOptions<InstrumentsOptions> options, InstrumentDirectory directory, IFrameJournal journal, IHostQueryResponder responder, Db db,
    LinkHealthRegistry registry, ILoggerFactory loggerFactory, TimeProvider time) : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<InstrumentLinkWorker>();

    private sealed record RunningLink(InstrumentConfig Config, CancellationTokenSource Stop, Task Loop);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RegisterInstrumentsAsync(stoppingToken);
        var running = new Dictionary<string, RunningLink>(StringComparer.Ordinal);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await directory.RefreshAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not load registered analyzers; keeping the links that run");
                }
                await ReconcileAsync(running, stoppingToken);
                try { await directory.WaitForChangeAsync(options.Value.RefreshInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            foreach (var link in running.Values) link.Stop.Cancel();
            await Task.WhenAll(running.Values.Select(l => l.Loop));
        }
    }

    private async Task ReconcileAsync(Dictionary<string, RunningLink> running, CancellationToken stoppingToken)
    {
        var desired = directory.All.Where(i => i.Enabled).ToDictionary(i => i.Id, StringComparer.Ordinal);
        foreach (var (id, link) in running.ToList())
        {
            if (desired.TryGetValue(id, out var cfg) && Same(cfg, link.Config)) continue;
            _logger.LogInformation("{Instrument}: stopping link ({Why})", id, cfg is null ? "disabled" : $"definition v{cfg.DefinitionVersion} approved");
            link.Stop.Cancel();
            try { await link.Loop.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (TimeoutException) { _logger.LogWarning("{Instrument}: link did not stop within 30 s", id); }
            running.Remove(id);
            var health = registry.Get(id);
            health.State = Astm.LinkState.Disconnected;
            health.LastError = cfg is null ? "disabled" : health.LastError;
        }
        foreach (var (id, cfg) in desired)
        {
            if (running.ContainsKey(id)) continue;
            var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            running[id] = new RunningLink(cfg, stop, RunLinkAsync(cfg, stop.Token));
        }
    }

    /// <summary>Configured analyzers keep their object; a registered one is the same while its definition version is.</summary>
    private static bool Same(InstrumentConfig a, InstrumentConfig b) =>
        ReferenceEquals(a, b) || (a.DefinitionId is not null && a.DefinitionId == b.DefinitionId);

    private async Task RegisterInstrumentsAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var conn = await db.OpenAsync(ct);
                foreach (var i in options.Value.Instruments)
                {
                    await conn.ExecuteAsync("""
                        INSERT INTO instruments.instrument (id, model, protocol, software_version, verification_status)
                        VALUES (@Id, @Model, @Parser, @SoftwareVersion, 'documented')
                        ON CONFLICT (id) DO UPDATE SET model=EXCLUDED.model, protocol=EXCLUDED.protocol,
                            verification_status = CASE WHEN instruments.instrument.software_version IS DISTINCT FROM EXCLUDED.software_version
                                                            AND instruments.instrument.verification_status = 'verified'
                                                       THEN 're-verify' ELSE instruments.instrument.verification_status END,
                            software_version=EXCLUDED.software_version
                        """, i);
                }
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 30)
            {
                _logger.LogWarning(ex, "Could not register instruments, retrying");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private async Task RunLinkAsync(InstrumentConfig cfg, CancellationToken ct)
    {
        await Task.Yield();
        var health = registry.Get(cfg.Id);
        var logger = loggerFactory.CreateLogger($"Lims.Instruments.Link.{cfg.Id}");
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (stream, owner) = await LinkTransports.OpenAsync(cfg.Transport, ct);
                using (owner)
                await using (stream)
                await using (ct.Register(() => owner?.Dispose())) // a stopped link closes its socket or port at once
                {
                    logger.LogInformation("{Instrument}: link open ({Mode})", cfg.Id, cfg.Transport.Mode);
                    health.LastError = null;
                    delay = TimeSpan.FromSeconds(1);
                    if (ParserRegistry.IsHl7(cfg.Parser))
                    {
                        var (dialect, encoding) = ParserRegistry.Hl7LinkFor(cfg);
                        await new Hl7.Hl7LinkSession(cfg.Id, journal, cfg.HostQuery ? responder : null, health, time, logger,
                            dialect, encoding, allowPush: cfg.Generic.PushesOrders && cfg.IsRegistered).RunAsync(stream, ct);
                    }
                    else
                        await new AstmLinkSession(cfg.Id, journal, cfg.HostQuery ? responder : null, cfg.Link, health, logger).RunAsync(stream, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                logger.LogDebug("{Instrument}: link stopped ({Error})", cfg.Id, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                health.LastError = ex.Message;
                health.State = LinkState.Disconnected;
                logger.LogWarning("{Instrument}: link error: {Error}; reconnect in {Delay}", cfg.Id, ex.Message, delay);
            }
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }
    }
}
