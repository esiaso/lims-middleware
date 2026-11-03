using Lims.Contracts;
using Lims.Instruments.Astm;
using Lims.Instruments.Hl7;
using Microsoft.Extensions.Options;

namespace Lims.Instruments;

/// <summary>Adapter registry (design 6.1): each instrument names its adapter by id; adding a model means adding an entry here.</summary>
public sealed class ParserRegistry(IOptions<InstrumentsOptions> options) : IInstrumentParserResolver
{
    public static readonly IReadOnlyList<string> KnownParsers = [CobasE411Parser.Id, CobasC111Parser.Id, MindrayBc5000Parser.Id];

    public static bool IsHl7(string parserId) => parserId == MindrayBc5000Parser.Id;

    public IMessageParser? ForInstrument(string instrumentId)
    {
        var cfg = options.Value.Instruments.FirstOrDefault(i => i.Id == instrumentId);
        return cfg is null ? null : Create(cfg);
    }

    public static IMessageParser? Create(InstrumentConfig cfg) => cfg.Parser switch
    {
        CobasE411Parser.Id => new CobasE411Parser(cfg.UtcOffset),
        CobasC111Parser.Id => new CobasC111Parser(cfg.UtcOffset),
        MindrayBc5000Parser.Id => new MindrayBc5000Parser(cfg.UtcOffset),
        _ => null,
    };
}

/// <summary>
/// Answers host queries (e411 and c111 TSREQ, BC-5000 ORM^O01) from accepted specimens only, via the clinical
/// module's lookup. Returns null when no reply should be sent (host query off, a cancel, or an unparsable query).
/// </summary>
public sealed class InstrumentQueryResponder(IOptions<InstrumentsOptions> options, IInstrumentOrderLookup lookup, TimeProvider time) : IHostQueryResponder
{
    public async Task<string?> BuildReplyAsync(string instrumentId, byte[] queryMessage, string correlationId, CancellationToken ct)
    {
        var cfg = options.Value.Instruments.First(i => i.Id == instrumentId);
        if (!cfg.HostQuery) return null;
        var parser = ParserRegistry.Create(cfg);
        if (parser is null) return null;
        var parsed = parser.Parse(instrumentId, queryMessage, correlationId);
        if (!parsed.Success) return null;
        var q = parsed.Messages.FirstOrDefault(m => m.Kind == MessageKind.Query);
        if (q?.Query is null || q.Query.IsCancel) return null;

        InstrumentOrder? order = null;
        if (q.SampleId is { } sid && !sid.StartsWith('@')) // "@" + sequence = unreadable barcode (e411 manual E-9)
            order = await lookup.GetOrdersForSampleAsync(instrumentId, sid, ct);

        return cfg.Parser switch
        {
            CobasE411Parser.Id => CobasE411QueryReply.Build(cfg.Variant, q, order),
            CobasC111Parser.Id => CobasC111QueryReply.Build(q, order, time.GetUtcNow().ToOffset(cfg.UtcOffset)),
            MindrayBc5000Parser.Id => MindrayBc5000Replies.WorklistReply(q.Query.SequenceNumber ?? "", q.SampleId, order, time.GetUtcNow().ToOffset(cfg.UtcOffset)),
            _ => null,
        };
    }
}
