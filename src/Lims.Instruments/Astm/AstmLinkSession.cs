using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Lims.Instruments.Astm;

public enum LinkState { Disconnected, Idle, Receiving, WaitingForAck, Sending }

/// <summary>Live counters shown on the integration health screen.</summary>
public sealed class LinkHealth
{
    public string InstrumentId { get; init; } = "";
    public LinkState State { get; set; } = LinkState.Disconnected;
    public DateTimeOffset? LastFrameAt { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public DateTimeOffset? ConnectedSince { get; set; }
    public int MessagesToday { get; set; }
    public int NaksSent { get; set; }
    public int ChecksumErrors { get; set; }
    public int Timeouts { get; set; }
    public int IncompleteMessages { get; set; }
    public int SendFailures { get; set; }
    public string? LastError { get; set; }
    public bool Paused { get; set; }
    public string? PausedBy { get; set; }
    /// <summary>Queues a host-initiated message (kind, text, correlation id) on the open link; null while disconnected or for HL7 links.</summary>
    [JsonIgnore] public Action<string, string, string>? EnqueueOutbound { get; set; }
}

/// <summary>
/// ASTM E1381 low-level link for one analyzer connection (design 6.3). Half duplex; the analyzer wins
/// line contention. Frames are journaled before ACK; a message (H..L) is committed to the inbox before
/// the ACK of its last frame, so an acknowledged result is never only in memory.
/// </summary>
public sealed class AstmLinkSession(
    string instrumentId,
    IFrameJournal journal,
    IHostQueryResponder? queryResponder,
    AstmLinkOptions options,
    LinkHealth health,
    ILogger logger)
{
    private readonly ConcurrentQueue<(string Kind, string Message, string CorrelationId)> _outbound = new();

    public void EnqueueOutbound(string kind, string message, string correlationId) => _outbound.Enqueue((kind, message, correlationId));

    public async Task RunAsync(Stream stream, CancellationToken ct)
    {
        var ch = new ByteChannel(stream);
        health.State = LinkState.Idle;
        health.ConnectedSince = DateTimeOffset.UtcNow;
        health.EnqueueOutbound = EnqueueOutbound;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!health.Paused && _outbound.TryPeek(out _))
                {
                    await SendPendingAsync(ch, ct);
                    continue;
                }
                var b = await ch.ReadByteAsync(options.IdlePoll, ct);
                if (b < 0) continue;
                if (b == AstmControl.ENQ) await ReceiveAsync(ch, ct);
                else if (b == AstmControl.EOT) { /* stray EOT while idle: ignore */ }
                else logger.LogDebug("{Instrument}: ignoring byte 0x{Byte:X2} while idle", instrumentId, b);
            }
        }
        finally
        {
            health.EnqueueOutbound = null;
            health.State = LinkState.Disconnected;
        }
    }

    // ------------------------------------------------------------------ receive

    private async Task ReceiveAsync(ByteChannel ch, CancellationToken ct)
    {
        if (health.Paused || !await journal.IsAvailableAsync(ct))
        {
            // Storage cannot take data: refuse so the analyzer keeps the results (design 7.6).
            await ch.WriteByteAsync(AstmControl.NAK, ct);
            health.NaksSent++;
            return;
        }
        await ch.WriteByteAsync(AstmControl.ACK, ct);
        health.State = LinkState.Receiving;

        var sessionId = Guid.CreateVersion7();
        var expectedFn = 1;
        var lastAcceptedFn = -1;
        var frameIndex = 0;
        var message = new List<byte>();
        var uncommittedFrames = 0;
        var queries = new List<(byte[] Message, string CorrelationId)>();

        try
        {
            while (true)
            {
                var b = await ch.ReadByteAsync(options.ReceiveTimeout, ct);
                if (b < 0)
                {
                    health.Timeouts++;
                    if (uncommittedFrames > 0 || message.Count > 0)
                    {
                        health.IncompleteMessages++;
                        await journal.MarkIncompleteAsync(instrumentId, sessionId, ct);
                    }
                    logger.LogWarning("{Instrument}: receive timeout, partial message kept for diagnostics", instrumentId);
                    return;
                }
                if (b == AstmControl.EOT)
                {
                    if (message.Count > 0)
                    {
                        // Transmission ended without an L record: keep the frames, do not guess.
                        health.IncompleteMessages++;
                        await journal.MarkIncompleteAsync(instrumentId, sessionId, ct);
                        logger.LogWarning("{Instrument}: EOT before message terminator record", instrumentId);
                    }
                    break;
                }
                if (b == AstmControl.ENQ)
                {
                    // Sender restarted the establishment phase.
                    await ch.WriteByteAsync(AstmControl.ACK, ct);
                    continue;
                }
                if (b != AstmControl.STX) continue;

                var raw = await ReadFrameBytesAsync(ch, ct);
                if (raw is null)
                {
                    health.Timeouts++;
                    await journal.MarkIncompleteAsync(instrumentId, sessionId, ct);
                    return;
                }
                var frame = AstmFrame.TryParse(raw, out var error);
                if (frame is null)
                {
                    health.ChecksumErrors++;
                    health.NaksSent++;
                    logger.LogWarning("{Instrument}: bad frame ({Error}), NAK", instrumentId, error);
                    await ch.WriteByteAsync(AstmControl.NAK, ct);
                    continue;
                }
                health.LastFrameAt = DateTimeOffset.UtcNow;

                if (frame.FrameNumber == lastAcceptedFn)
                {
                    // Our ACK was lost and the analyzer resent the same frame: acknowledge, discard.
                    await ch.WriteByteAsync(AstmControl.ACK, ct);
                    continue;
                }
                if (frame.FrameNumber != expectedFn)
                {
                    health.NaksSent++;
                    logger.LogWarning("{Instrument}: frame number {Got} != expected {Expected}, NAK", instrumentId, frame.FrameNumber, expectedFn);
                    await ch.WriteByteAsync(AstmControl.NAK, ct);
                    continue;
                }

                var lengthBefore = message.Count;
                message.AddRange(frame.Text);
                if (frame.IsFinal && (message.Count == 0 || message[^1] != AstmControl.CR)) message.Add(AstmControl.CR);
                var linkFrame = new LinkFrame(sessionId, frameIndex, frame.FrameNumber, frame.Text, frame.IsFinal);

                try
                {
                    if (frame.IsFinal && EndsWithTerminator(message))
                    {
                        var bytes = message.ToArray();
                        var correlationId = Guid.CreateVersion7().ToString();
                        await journal.CommitMessageAsync(instrumentId, linkFrame, bytes, correlationId, ct);
                        health.LastMessageAt = DateTimeOffset.UtcNow;
                        health.MessagesToday++;
                        if (ContainsQuery(bytes)) queries.Add((bytes, correlationId));
                        message.Clear();
                        uncommittedFrames = 0;
                        sessionId = Guid.CreateVersion7();
                        frameIndex = 0;
                    }
                    else
                    {
                        await journal.AppendFrameAsync(instrumentId, linkFrame, ct);
                        uncommittedFrames++;
                        frameIndex++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not stored, so not acknowledged: the analyzer retransmits or reports an error.
                    message.RemoveRange(lengthBefore, message.Count - lengthBefore);
                    health.LastError = ex.Message;
                    health.NaksSent++;
                    logger.LogError(ex, "{Instrument}: could not store frame, NAK", instrumentId);
                    await ch.WriteByteAsync(AstmControl.NAK, ct);
                    continue;
                }

                await ch.WriteByteAsync(AstmControl.ACK, ct);
                lastAcceptedFn = frame.FrameNumber;
                expectedFn = (expectedFn + 1) % 8;
            }
        }
        finally
        {
            health.State = LinkState.Idle;
        }

        // Answer host queries after the analyzer has released the line (EOT).
        if (queryResponder is not null)
        {
            foreach (var (q, correlationId) in queries)
            {
                try
                {
                    var reply = await queryResponder.BuildReplyAsync(instrumentId, q, correlationId, ct);
                    if (reply is not null) EnqueueOutbound("query.reply", reply, correlationId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "{Instrument}: could not build query reply", instrumentId);
                }
            }
        }
    }

    private async Task<byte[]?> ReadFrameBytesAsync(ByteChannel ch, CancellationToken ct)
    {
        var buf = new List<byte>(256) { AstmControl.STX };
        while (buf.Count <= 300)
        {
            var b = await ch.ReadByteAsync(options.ReceiveTimeout, ct);
            if (b < 0) return null;
            buf.Add((byte)b);
            if (b == AstmControl.LF && buf.Count >= 2 && buf[^2] == AstmControl.CR) return buf.ToArray();
        }
        return buf.ToArray(); // oversize: TryParse rejects it and we NAK
    }

    private static bool EndsWithTerminator(List<byte> message)
    {
        // Last record in the buffer starts with 'L' + field delimiter.
        var end = message.Count - 1;
        while (end >= 0 && message[end] == AstmControl.CR) end--;
        if (end < 0) return false;
        var start = end;
        while (start > 0 && message[start - 1] != AstmControl.CR) start--;
        return end - start >= 1 && (message[start] == (byte)'L' || message[start] == (byte)'l') && message[start + 1] == (byte)'|';
    }

    private static bool ContainsQuery(byte[] message)
    {
        var text = Encoding.ASCII.GetString(message);
        return text.Contains("\rQ|", StringComparison.Ordinal) || text.StartsWith("Q|", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ send

    private async Task SendPendingAsync(ByteChannel ch, CancellationToken ct)
    {
        if (!_outbound.TryPeek(out var item)) return;
        var frames = AstmFrame.BuildFrames(item.Message);
        var outcome = await SendMessageAsync(ch, frames, ct);
        if (outcome == SendOutcome.Contention) return; // keep it queued, retry after the analyzer's message
        _outbound.TryDequeue(out _);
        if (outcome != SendOutcome.Sent) health.SendFailures++;
        await journal.RecordOutboundAsync(instrumentId, item.Kind, Encoding.ASCII.GetBytes(item.Message), item.CorrelationId, outcome.ToString(), ct);
    }

    private enum SendOutcome { Sent, Contention, Failed }

    private async Task<SendOutcome> SendMessageAsync(ByteChannel ch, IReadOnlyList<AstmFrame> frames, CancellationToken ct)
    {
        // Establishment phase
        for (var attempt = 1; ; attempt++)
        {
            health.State = LinkState.WaitingForAck;
            await ch.WriteByteAsync(AstmControl.ENQ, ct);
            var reply = await ch.ReadByteAsync(options.AckTimeout, ct);
            if (reply == AstmControl.ACK) break;
            if (reply == AstmControl.ENQ)
            {
                // Contention: the analyzer has priority (design 6.3).
                await ReceiveAsync(ch, ct);
                await Task.Delay(options.ContentionRetryDelay, ct);
                return SendOutcome.Contention;
            }
            if (reply == AstmControl.NAK && attempt < options.MaxSendAttempts)
            {
                await Task.Delay(options.NakRetryDelay, ct);
                continue;
            }
            if (reply < 0) health.Timeouts++;
            await ch.WriteByteAsync(AstmControl.EOT, ct);
            health.State = LinkState.Idle;
            logger.LogWarning("{Instrument}: establishment failed (reply {Reply})", instrumentId, reply);
            return SendOutcome.Failed;
        }

        // Transfer phase
        health.State = LinkState.Sending;
        foreach (var frame in frames)
        {
            var bytes = frame.ToBytes();
            var sent = false;
            for (var attempt = 1; attempt <= options.MaxSendAttempts && !sent; attempt++)
            {
                await ch.WriteAsync(bytes, ct);
                var reply = await ch.ReadByteAsync(options.AckTimeout, ct);
                if (reply == AstmControl.ACK) sent = true;
                else if (reply == AstmControl.EOT) sent = true; // receiver interrupt request: treat as ACK, finish
                else if (reply < 0) { health.Timeouts++; break; }
            }
            if (!sent)
            {
                await ch.WriteByteAsync(AstmControl.EOT, ct);
                health.State = LinkState.Idle;
                return SendOutcome.Failed;
            }
        }
        await ch.WriteByteAsync(AstmControl.EOT, ct);
        health.State = LinkState.Idle;
        return SendOutcome.Sent;
    }
}
