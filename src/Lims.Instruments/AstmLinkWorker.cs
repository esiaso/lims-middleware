using System.Collections.Concurrent;
using Dapper;
using Lims.Instruments.Astm;
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

/// <summary>One link loop per enabled instrument: open transport, run the ASTM session, reconnect with backoff 1 s to 60 s.</summary>
public sealed class AstmLinkWorker(
    IOptions<InstrumentsOptions> options, IFrameJournal journal, IHostQueryResponder responder, Db db,
    LinkHealthRegistry registry, ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AstmLinkWorker>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RegisterInstrumentsAsync(stoppingToken);
        var loops = options.Value.Instruments.Where(i => i.Enabled).Select(i => RunLinkAsync(i, stoppingToken)).ToList();
        await Task.WhenAll(loops);
    }

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
                {
                    logger.LogInformation("{Instrument}: link open ({Mode})", cfg.Id, cfg.Transport.Mode);
                    delay = TimeSpan.FromSeconds(1);
                    var session = new AstmLinkSession(cfg.Id, journal, cfg.HostQuery ? responder : null, cfg.Link, health, logger);
                    await session.RunAsync(stream, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
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
