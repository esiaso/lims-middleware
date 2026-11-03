namespace Lims.Instruments.Astm;

public sealed record LinkFrame(Guid SessionId, int FrameIndex, int FrameNumber, byte[] Text, bool IsFinal);

/// <summary>
/// Durable store used by the link loop. Every method must have committed to disk before it returns,
/// because the caller sends ACK right after.
/// </summary>
public interface IFrameJournal
{
    /// <summary>False when storage cannot accept data (database down, disk critical): the link answers NAK.</summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken ct);

    Task AppendFrameAsync(string instrumentId, LinkFrame frame, CancellationToken ct);

    /// <summary>Journals the last frame of a message and stores the assembled message in the inbox, in one transaction.</summary>
    Task<Guid> CommitMessageAsync(string instrumentId, LinkFrame lastFrame, byte[] message, string correlationId, CancellationToken ct);

    /// <summary>Stores a whole message received in one piece (HL7 over MLLP) in the inbox before it is acknowledged.</summary>
    Task<Guid> CommitWholeMessageAsync(string instrumentId, string kind, byte[] message, string correlationId, CancellationToken ct);

    Task MarkIncompleteAsync(string instrumentId, Guid sessionId, CancellationToken ct);

    Task RecordOutboundAsync(string instrumentId, string kind, byte[] message, string correlationId, string outcome, CancellationToken ct);
}

/// <summary>Builds the reply to a host query. Returns null when no reply should be sent (for example a cancel).</summary>
public interface IHostQueryResponder
{
    Task<string?> BuildReplyAsync(string instrumentId, byte[] queryMessage, string correlationId, CancellationToken ct);
}
