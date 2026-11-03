using Lims.Contracts;
using Lims.Instruments.Astm;
using Lims.Instruments.Generic;
using Lims.Instruments.Hl7;
using Lims.Instruments.Registration;

namespace Lims.Instruments;

/// <summary>
/// Adapter registry (design 6.1): each instrument names its adapter by id. The built-in adapters are written from a
/// vendor manual; any other analyzer uses a generic ASTM or HL7 adapter configured from the Analyzer setup screen.
/// </summary>
public sealed class ParserRegistry(InstrumentDirectory directory) : IInstrumentParserResolver
{
    public static readonly IReadOnlyList<string> BuiltInParsers = [CobasE411Parser.Id, CobasC111Parser.Id, MindrayBc5000Parser.Id];
    public static readonly IReadOnlyList<string> KnownParsers = [.. BuiltInParsers, GenericAstmParser.Id, GenericHl7Parser.Id];

    public static bool IsHl7(string parserId) => parserId is MindrayBc5000Parser.Id or GenericHl7Parser.Id;

    public IMessageParser? ForInstrument(string instrumentId)
    {
        var cfg = directory.Find(instrumentId);
        return cfg is null ? null : Create(cfg);
    }

    public static IMessageParser? Create(InstrumentConfig cfg) => cfg.Parser switch
    {
        CobasE411Parser.Id => new CobasE411Parser(cfg.UtcOffset),
        CobasC111Parser.Id => new CobasC111Parser(cfg.UtcOffset),
        MindrayBc5000Parser.Id => new MindrayBc5000Parser(cfg.UtcOffset),
        GenericAstmParser.Id => new GenericAstmParser(cfg.Generic, cfg.UtcOffset),
        GenericHl7Parser.Id => new GenericHl7Parser(cfg.Generic, cfg.UtcOffset),
        _ => null,
    };

    /// <summary>The HL7 dialect and text encoding of an HL7 link.</summary>
    public static (IHl7Dialect Dialect, System.Text.Encoding Encoding) Hl7LinkFor(InstrumentConfig cfg) => cfg.Parser == GenericHl7Parser.Id
        ? (new GenericHl7Dialect(cfg.Generic), cfg.Generic.TextEncoding())
        : (MindrayBc5000Dialect.Instance, new System.Text.UTF8Encoding(false));
}

/// <summary>
/// Answers host queries (e411 and c111 TSREQ, BC-5000 ORM^O01, registered analyzers' configured query) from accepted specimens only, via the clinical
/// module's lookup. Returns null when no reply should be sent (host query off, a cancel, or an unparsable query).
/// </summary>
public sealed class InstrumentQueryResponder(InstrumentDirectory directory, IInstrumentOrderLookup lookup, TimeProvider time) : IHostQueryResponder
{
    public async Task<string?> BuildReplyAsync(string instrumentId, byte[] queryMessage, string correlationId, CancellationToken ct)
    {
        var cfg = directory.Find(instrumentId);
        if (cfg is null) return null;
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
            GenericAstmParser.Id => GenericAstmMessages.Order(cfg.Generic, q.SampleId ?? "", order, time.GetUtcNow().ToOffset(cfg.UtcOffset)),
            GenericHl7Parser.Id => GenericHl7Messages.QueryReply(cfg.Generic, q.Query.SequenceNumber ?? "", q.SampleId, order, time.GetUtcNow().ToOffset(cfg.UtcOffset)),
            _ => null,
        };
    }
}
