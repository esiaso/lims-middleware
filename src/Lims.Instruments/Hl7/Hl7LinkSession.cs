using System.Text;
using Lims.Contracts;
using Lims.Instruments.Astm;
using Microsoft.Extensions.Logging;

namespace Lims.Instruments.Hl7;

/// <summary>
/// HL7 over MLLP link for one analyzer connection (design 6.4). Every received message is committed to the inbox
/// before it is acknowledged; a commit failure is answered with MSA-1 AE (207) so nothing is acknowledged that is
/// only in memory. Worklist queries (ORM^O01) are answered with ORR^O02 on the same connection.
/// </summary>
public sealed class Hl7LinkSession(
    string instrumentId,
    IFrameJournal journal,
    IHostQueryResponder? queryResponder,
    LinkHealth health,
    TimeProvider time,
    ILogger logger)
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public async Task RunAsync(Stream stream, CancellationToken ct)
    {
        await using var buffered = new BufferedStream(stream, 64 * 1024);
        health.State = LinkState.Idle;
        health.ConnectedSince = time.GetUtcNow();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var payload = await Mllp.ReadAsync(buffered, ct);
                if (payload is null) return; // peer closed between messages
                health.State = LinkState.Receiving;
                health.LastFrameAt = time.GetUtcNow();
                var reply = await HandleAsync(payload, ct);
                await buffered.WriteAsync(Mllp.Frame(Utf8.GetBytes(reply.Text)), ct);
                await buffered.FlushAsync(ct);
                if (reply.Kind is not null)
                    await journal.RecordOutboundAsync(instrumentId, reply.Kind, Utf8.GetBytes(reply.Text), reply.CorrelationId, "Sent", ct);
                health.State = LinkState.Idle;
            }
        }
        finally
        {
            health.State = LinkState.Disconnected;
        }
    }

    private sealed record Reply(string Text, string? Kind, string CorrelationId);

    private async Task<Reply> HandleAsync(byte[] payload, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var correlationId = Guid.CreateVersion7().ToString();
        string text;
        try { text = new UTF8Encoding(false, true).GetString(payload); }
        catch (System.Text.DecoderFallbackException) { text = Encoding.Latin1.GetString(payload); }
        var msg = Hl7Message.TryParse(text, out _);
        var controlId = msg?.ControlId ?? "";
        var processingId = msg?.ProcessingId ?? "P";

        if (health.Paused || !await journal.IsAvailableAsync(ct))
        {
            // Paused by an operator, or storage cannot take data: refuse so the analyzer keeps the results (design 7.6).
            health.NaksSent++;
            logger.LogWarning("{Instrument}: {Why}, answering AE", instrumentId, health.Paused ? "link paused" : "storage unavailable");
            return new Reply(MindrayBc5000Replies.Ack("AE", controlId, processingId, now, 207, "Application internal error"), null, correlationId);
        }

        try
        {
            await journal.CommitWholeMessageAsync(instrumentId, InboxKinds.Hl7Message, payload, correlationId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            health.NaksSent++;
            health.LastError = ex.Message;
            logger.LogError(ex, "{Instrument}: could not store message, answering AE", instrumentId);
            return new Reply(MindrayBc5000Replies.Ack("AE", controlId, processingId, now, 207, "Application internal error"), null, correlationId);
        }
        health.LastMessageAt = time.GetUtcNow();
        health.MessagesToday++;

        // Stored. Now say whether we could use it (the inbox handler quarantines anything it cannot parse).
        if (msg is null)
        {
            health.ChecksumErrors++;
            return new Reply(MindrayBc5000Replies.Ack("AE", "", "P", now, 100, "Segment sequence error"), null, correlationId);
        }
        if (!msg.Version.StartsWith("2.", StringComparison.Ordinal))
            return new Reply(MindrayBc5000Replies.Ack("AR", controlId, processingId, now, 203, "Unsupported version id"), null, correlationId);

        switch (msg.MessageType, msg.TriggerEvent)
        {
            case ("ORU", "R01"):
                return msg.First("OBR") is null
                    ? new Reply(MindrayBc5000Replies.Ack("AE", controlId, processingId, now, 100, "Segment sequence error"), null, correlationId)
                    : new Reply(MindrayBc5000Replies.Ack("AA", controlId, processingId, now), null, correlationId);
            case ("ORM", "O01"):
            {
                var orc = msg.First("ORC");
                if (orc is null)
                    return new Reply(MindrayBc5000Replies.Ack("AE", controlId, processingId, now, 100, "Segment sequence error"), null, correlationId);
                string? reply = null;
                if (queryResponder is not null)
                {
                    try { reply = await queryResponder.BuildReplyAsync(instrumentId, payload, correlationId, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "{Instrument}: could not build worklist reply", instrumentId); }
                }
                reply ??= MindrayBc5000Replies.WorklistReply(controlId, null, null, now); // no order known (or host query off)
                return new Reply(reply, "query.reply", correlationId);
            }
            case ("ORM", _) or ("ORU", _):
                return new Reply(MindrayBc5000Replies.Ack("AR", controlId, processingId, now, 201, "Unsupported event code"), null, correlationId);
            default:
                return new Reply(MindrayBc5000Replies.Ack("AR", controlId, processingId, now, 200, "Unsupported message type"), null, correlationId);
        }
    }
}
