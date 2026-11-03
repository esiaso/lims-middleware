using Dapper;
using Lims.Platform;
using Microsoft.Extensions.Options;

namespace Lims.Instruments.Registration;

/// <summary>
/// Every analyzer the middleware knows: the ones in the service configuration (appsettings) and the ones registered
/// and approved from the screens. A configured ID always wins over a registered one. Read by the link worker, the
/// parser registry, query replies, order download and the screens.
/// </summary>
public sealed class InstrumentDirectory(IOptions<InstrumentsOptions> options, Db db)
{
    private volatile IReadOnlyList<InstrumentConfig> _registered = [];
    private readonly SemaphoreSlim _changed = new(0);

    public IReadOnlyList<InstrumentConfig> Configured => options.Value.Instruments;

    public IReadOnlyList<InstrumentConfig> Registered => _registered;

    public IReadOnlyList<InstrumentConfig> All
    {
        get
        {
            var configured = Configured;
            return configured.Concat(_registered.Where(r => configured.All(c => c.Id != r.Id))).ToList();
        }
    }

    public InstrumentConfig? Find(string id) =>
        Configured.FirstOrDefault(i => i.Id == id) ?? _registered.FirstOrDefault(i => i.Id == id);

    public bool IsConfigured(string id) => Configured.Any(i => i.Id == id);

    /// <summary>Reloads the active analyzer definitions (enabled and disabled) from the database.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(Guid Id, int Version, bool Enabled, string Definition)>(
            "SELECT id, version, enabled, definition::text FROM instruments.analyzer_definition WHERE status='active' ORDER BY instrument_id");
        _registered = rows.Select(r => AnalyzerDefinition.FromJson(r.Definition).ToConfig(r.Id, r.Version, r.Enabled)).ToList();
    }

    /// <summary>Wakes the link worker so a change takes effect now rather than at the next refresh.</summary>
    public void NotifyChanged() => _changed.Release();

    public async Task WaitForChangeAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (await _changed.WaitAsync(timeout, ct))
            while (_changed.CurrentCount > 0) await _changed.WaitAsync(0, ct);
    }
}
