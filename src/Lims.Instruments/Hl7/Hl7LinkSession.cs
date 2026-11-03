using System.Collections.Concurrent;
using System.Text;
using Lims.Contracts;
using Lims.Instruments.Astm;
using Microsoft.Extensions.Logging;

namespace Lims.Instruments.Hl7;

/// <summary>
/// HL7 over MLLP link for one analyzer connection (design 6.4). Every received message is committed to the inbox
/// before it is acknowledged; a commit failure is answered with MSA-1 AE (207) so nothing is acknowledged that is
/// only in memory. Order queries are answered on the same connection. When <paramref name="allowPush"/> is set,
/// host-initiated messages (pushed orders) are queued with <see cref="EnqueueOutbound"/>; the analyzer's ACK is logged.
/// </summary>
public sealed class Hl7LinkSession(
    string instrumentId,
    IFrameJournal journal,
    IHostQueryResponder? queryResponder,
    LinkHealth health,
    TimeProvider time,
    ILogger logger,
    IHl7Dialect? dialect = null,
    Encoding? encoding = null,
    bool allowPush = false)
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly IHl7Dialect _dialect = dialect ?? MindrayBc5000Dialect.Instance;
    private readonly Encoding _encoding = encoding ?? Utf8;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentQueue<(string Kind, string Message, string CorrelationId)> _outbound = new();
    private readonly SemaphoreSlim _outboundSignal = new(0);

    public void EnqueueOutbound(string kind, string message, string correlationId)
    {
        _outbound.Enqueue((kind, message, correlationId));
        _outboundSignal.Release();
    }

    public async Task RunAsync(Stream stream, CancellationToken ct)
    {
        // Reads go through the buffer; writes go straight to the stream under a lock, so a pushed order and a reply never interleave.
        await using var buffered = new BufferedStream(stream, 64 * 1024);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        health.State = LinkState.Idle;
        health.ConnectedSince = time.GetUtcNow();
        if (allowPush) health.EnqueueOutbound = EnqueueOutbound;
        var pump = allowPush ? PumpOutboundAsync(stream, sessionCts.Token) : Task.CompletedTask;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var payload = await Mllp.ReadAsync(buffered, ct);
                if (payload is null) return; // peer closed between messages
                health.State = LinkState.Receiving;
                health.LastFrameAt = time.GetUtcNow();
                var reply = await HandleAsync(payload, ct);
                if (reply is not null)
                {
                    await WriteAsync(stream, reply.Text, ct);
                    if (reply.Kind is not null)
                        await journal.RecordOutboundAsync(instrumentId, reply.Kind, _encoding.GetBytes(reply.Text), reply.CorrelationId, "Sent", ct);
                }
                health.State = LinkState.Idle;
            }
        }
        finally
        {
            health.EnqueueOutbound = null;
            health.State = LinkState.Disconnected;
            await sessionCts.CancelAsync();
            try { await pump; } catch (OperationCanceledException) { /* session ended */ }
        }
    }

    private async Task WriteAsync(Stream stream, string text, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(Mllp.Frame(_encoding.GetBytes(text)), ct);
            await stream.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    private async Task PumpOutboundAsync(Stream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _outboundSignal.WaitAsync(ct);
            if (health.Paused)
            {
                // A paused link sends nothing: keep the message and look again shortly.
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                _outboundSignal.Release();
                continue;
            }
            if (!_outbound.TryDequeue(out var item)) continue;
            var outcome = "Sent";
            try
            {
                health.State = LinkState.Sending;
                await WriteAsync(stream, item.Message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = "Failed";
                health.SendFailures++;
                health.LastError = ex.Message;
                logger.LogWarning("{Instrument}: could not send {Kind}: {Error}", instrumentId, item.Kind, ex.Message);
            }
            try { await journal.RecordOutboundAsync(instrumentId, item.Kind, _encoding.GetBytes(item.Message), item.CorrelationId, outcome, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "{Instrument}: could not record outbound {Kind}", instrumentId, item.Kind); }
            health.State = LinkState.Idle;
        }
    }

    private sealed record Reply(string Text, string? Kind, string CorrelationId);

    private async Task<Reply?> HandleAsync(byte[] payload, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var correlationId = Guid.CreateVersion7().ToString();
        string text;
        try { text = _encoding is UTF8Encoding ? new UTF8Encoding(false, true).GetString(payload) : _encoding.GetString(payload); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(payload); }
        var msg = Hl7Message.TryParse(text, out _);
        var kind = msg is null ? Hl7Inbound.Malformed : _dialect.Classify(msg);

        if (kind == Hl7Inbound.Acknowledgement)
        {
            // The analyzer acknowledging a pushed order: nothing to store and nothing to answer.
            var msa = msg!.First("MSA");
            logger.LogInformation("{Instrument}: analyzer acknowledged {ControlId} with {Code}", instrumentId, msa?.Text(2), msa?.Text(1));
            if (msa is not null && msa.Text(1).Trim() is not ("AA" or "CA")) { health.SendFailures++; health.LastError = $"analyzer answered {msa.Text(1)} to {msa.Text(2)}"; }
            return null;
        }

        if (health.Paused || !await journal.IsAvailableAsync(ct))
        {
            // Paused by an operator, or storage cannot take data: refuse so the analyzer keeps the results (design 7.6).
            health.NaksSent++;
            logger.LogWarning("{Instrument}: {Why}, answering AE", instrumentId, health.Paused ? "link paused" : "storage unavailable");
            return new Reply(_dialect.Ack("AE", msg, now, 207, "Application internal error"), null, correlationId);
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
            return new Reply(_dialect.Ack("AE", msg, now, 207, "Application internal error"), null, correlationId);
        }
        health.LastMessageAt = time.GetUtcNow();
        health.MessagesToday++;

        // Stored. Now say whether we could use it (the inbox handler quarantines anything it cannot parse).
        if (msg is null)
        {
            health.ChecksumErrors++;
            return new Reply(_dialect.Ack("AE", null, now, 100, "Segment sequence error"), null, correlationId);
        }
        if (!msg.Version.StartsWith("2.", StringComparison.Ordinal))
            return new Reply(_dialect.Ack("AR", msg, now, 203, "Unsupported version id"), null, correlationId);

        switch (kind)
        {
            case Hl7Inbound.Result:
                return new Reply(_dialect.Ack("AA", msg, now), null, correlationId);
            case Hl7Inbound.Query:
            {
                string? reply = null;
                if (queryResponder is not null)
                {
                    try { reply = await queryResponder.BuildReplyAsync(instrumentId, payload, correlationId, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "{Instrument}: could not build worklist reply", instrumentId); }
                }
                reply ??= _dialect.EmptyQueryReply(msg, now); // no order known (or host query off)
                return new Reply(reply, "query.reply", correlationId);
            }
            case Hl7Inbound.Malformed:
                return new Reply(_dialect.Ack("AE", msg, now, 100, "Segment sequence error"), null, correlationId);
            case Hl7Inbound.UnsupportedEvent:
                return new Reply(_dialect.Ack("AR", msg, now, 201, "Unsupported event code"), null, correlationId);
            default:
                return new Reply(_dialect.Ack("AR", msg, now, 200, "Unsupported message type"), null, correlationId);
        }
    }
}
