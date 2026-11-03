using System.Globalization;
using Lims.Audit;
using Lims.Contracts;
using Lims.Instruments.Astm;
using Lims.Instruments.Generic;
using Lims.Instruments.Hl7;
using Lims.Instruments.Registration;
using Lims.Platform;

namespace Lims.Instruments;

/// <summary>
/// Host-initiated order download ("resend order", design 8). Built from the manuals' batch examples:
/// e411 Host Interface Manual v2.2 "Batch test selection information" (cobas type only) and c111 manual 9.11.
/// The BC-5000 asks for its worklist when it reads a tube, so there is nothing to push: rescan the tube.
/// Registered analyzers push only when their worklist mode includes Push (generic ASTM or HL7 order message).
/// DOCUMENTED, NOT interface-tested.
/// </summary>
public static class OrderDownload
{
    public static string? Build(InstrumentConfig cfg, InstrumentOrder order, DateTimeOffset now)
    {
        var tests = order.InstrumentTestCodes.Distinct().ToList();
        if (tests.Count == 0) return null;
        var priority = order.Priority == "stat" ? "S" : "R";
        switch (cfg.Parser)
        {
            case CobasE411Parser.Id when cfg.Variant == E411ProtocolVariant.Cobas:
            {
                var o = Fields(26);
                o[0] = "O"; o[1] = "1"; o[2] = order.SampleId; o[3] = "^^^^S1^SC";
                o[4] = string.Join("\\", tests.Take(CobasE411QueryReply.MaxTestsPerOrder).Select(c => $"^^^{c}^"));
                o[5] = priority; o[11] = "A"; o[15] = "1"; o[25] = "O";
                return Message("H|\\^&|||host^1|||||cobas-e411|TSDWN^BATCH|P|1", o);
            }
            case CobasC111Parser.Id:
            {
                var o = Fields(26);
                o[0] = "O"; o[1] = "1"; o[2] = order.SampleId;
                o[4] = string.Join("\\", tests.Take(CobasC111QueryReply.MaxTestsPerOrder).Select(c => $"^^^{c}"));
                o[5] = priority; o[11] = "A"; o[22] = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture); o[25] = "O";
                return Message($"H|\\^&|||host^1|||||c111|TSDWN^BATCH|P|1|{now:yyyyMMddHHmmss}", o);
            }
            case GenericAstmParser.Id when cfg.Generic.PushesOrders:
                return GenericAstmMessages.Order(cfg.Generic, order.SampleId, order, now);
            case GenericHl7Parser.Id when cfg.Generic.PushesOrders:
                return GenericHl7Messages.Order(cfg.Generic, order, now);
            default:
                return null;
        }
    }

    /// <summary>Whether the screens offer "Resend order" for this analyzer.</summary>
    public static bool Supports(InstrumentConfig cfg) => cfg.Parser switch
    {
        CobasE411Parser.Id => cfg.HostQuery && cfg.Variant == E411ProtocolVariant.Cobas,
        CobasC111Parser.Id => cfg.HostQuery,
        GenericAstmParser.Id or GenericHl7Parser.Id => cfg.Generic.PushesOrders,
        _ => false,
    };

    private static string[] Fields(int n) { var a = new string[n]; Array.Fill(a, ""); return a; }
    private static string Message(string header, string[] o) => string.Join("\r", header, "P|1", string.Join("|", o), "L|1|N") + "\r";
}

/// <summary>Resends a specimen's order to an analyzer on operator request; audited.</summary>
public sealed class OrderResendService(InstrumentDirectory directory, IInstrumentOrderLookup lookup, LinkHealthRegistry links,
    Db db, AuditWriter audit, TimeProvider time)
{
    public async Task<string> ResendAsync(string instrumentId, string sampleId, string user, CancellationToken ct)
    {
        var cfg = directory.Find(instrumentId) ?? throw new WorkflowException("Unknown instrument");
        if (cfg.Parser == MindrayBc5000Parser.Id)
            throw new WorkflowException("The BC-5000 requests its worklist when it reads the tube: rescan the tube on the analyzer");
        var order = await lookup.GetOrdersForSampleAsync(instrumentId, sampleId, ct)
            ?? throw new WorkflowException("No accepted specimen with open tests for this instrument");
        var message = OrderDownload.Build(cfg, order, time.GetUtcNow().ToOffset(cfg.UtcOffset))
            ?? throw new WorkflowException("This instrument or protocol variant has no documented order download");
        var enqueue = links.Get(instrumentId).EnqueueOutbound ?? throw new WorkflowException("The analyzer link is not connected");
        var correlationId = Guid.CreateVersion7().ToString();
        enqueue("order.download", message, correlationId);
        await audit.WriteAsync(db, new AuditEntry(user, "instrument.order_resent", "instrument", instrumentId, Accession: sampleId,
            CorrelationId: correlationId, After: new { tests = order.InstrumentTestCodes }), ct);
        return correlationId;
    }
}
