using Lims.Instruments.Astm;

namespace Lims.Capture;

/// <summary>
/// Journal for acknowledge-only capture: each complete message is written to its own file before the link session
/// acknowledges it, exactly as the middleware commits to PostgreSQL first. Nothing is parsed, filed or answered.
/// </summary>
public sealed class MessageFileJournal(string directory) : IFrameJournal
{
    private int _seq = MessageFiles.NextIndex(directory);

    public ValueTask<bool> IsAvailableAsync(CancellationToken ct) => ValueTask.FromResult(true);
    public Task AppendFrameAsync(string instrumentId, LinkFrame frame, CancellationToken ct) => Task.CompletedTask;

    public Task<Guid> CommitMessageAsync(string instrumentId, LinkFrame lastFrame, byte[] message, string correlationId, CancellationToken ct) =>
        SaveAsync(message, ct);

    public Task<Guid> CommitWholeMessageAsync(string instrumentId, string kind, byte[] message, string correlationId, CancellationToken ct) =>
        SaveAsync(message, ct);

    public Task MarkIncompleteAsync(string instrumentId, Guid sessionId, CancellationToken ct)
    {
        Console.WriteLine("  incomplete message (link dropped or EOT before the terminator); the raw bytes are in the capture log");
        return Task.CompletedTask;
    }

    public Task RecordOutboundAsync(string instrumentId, string kind, byte[] message, string correlationId, string outcome, CancellationToken ct) =>
        Task.CompletedTask;

    private async Task<Guid> SaveAsync(byte[] message, CancellationToken ct)
    {
        var path = MessageFiles.Save(directory, Interlocked.Increment(ref _seq), "rx", message);
        await Task.CompletedTask;
        Console.WriteLine($"  saved {Path.GetFileName(path)} ({message.Length} bytes)");
        return Guid.NewGuid();
    }
}

public static class MessageFiles
{
    public static int NextIndex(string directory) =>
        !Directory.Exists(directory) ? 0
        : Directory.EnumerateFiles(directory, "*.msg").Select(f => int.TryParse(Path.GetFileName(f).Split('-')[0], out var n) ? n : 0).DefaultIfEmpty(0).Max();

    public static string Save(string directory, int index, string dir, byte[] message)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{index:D4}-{dir}.msg");
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        fs.Write(message);
        fs.Flush(flushToDisk: true);
        return path;
    }
}
