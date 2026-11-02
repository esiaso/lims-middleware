using Lims.Contracts;
using Lims.Instruments.Astm;
using Microsoft.Extensions.Options;

namespace Lims.Instruments;

public sealed class ParserRegistry(IOptions<InstrumentsOptions> options) : IInstrumentParserResolver
{
    public IMessageParser? ForInstrument(string instrumentId)
    {
        var cfg = options.Value.Instruments.FirstOrDefault(i => i.Id == instrumentId);
        if (cfg is null) return null;
        return cfg.Parser switch
        {
            CobasE411Parser.Id => new CobasE411Parser(cfg.UtcOffset),
            _ => null,
        };
    }
}

/// <summary>Answers e411 host queries from accepted specimens (via the clinical module's lookup).</summary>
public sealed class E411QueryResponder(IOptions<InstrumentsOptions> options, IInstrumentOrderLookup lookup) : IHostQueryResponder
{
    public async Task<string?> BuildReplyAsync(string instrumentId, byte[] queryMessage, string correlationId, CancellationToken ct)
    {
        var cfg = options.Value.Instruments.First(i => i.Id == instrumentId);
        if (!cfg.HostQuery) return null;
        var parsed = new CobasE411Parser(cfg.UtcOffset).Parse(instrumentId, queryMessage, correlationId);
        if (!parsed.Success) return null;
        var q = parsed.Messages.FirstOrDefault(m => m.Kind == MessageKind.Query);
        if (q?.Query is null || q.Query.IsCancel) return null;
        InstrumentOrder? order = null;
        if (q.SampleId is { } sid && !sid.StartsWith('@')) // "@" + sequence = unreadable barcode (manual E-9)
            order = await lookup.GetOrdersForSampleAsync(instrumentId, sid, ct);
        return CobasE411QueryReply.Build(cfg.Variant, q, order);
    }
}
