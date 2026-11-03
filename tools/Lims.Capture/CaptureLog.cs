using System.Text.Json;

namespace Lims.Capture;

/// <summary>One line per read or write on the link: UTC time, direction (rx from the analyzer, tx to it) and the bytes in hex.</summary>
public sealed record CaptureEntry(DateTimeOffset T, string Dir, string Hex);

public sealed class CaptureLog : IDisposable
{
    private readonly StreamWriter _w;
    private readonly Lock _lock = new();

    public CaptureLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public void Write(string dir, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        var line = JsonSerializer.Serialize(new CaptureEntry(DateTimeOffset.UtcNow, dir, Convert.ToHexString(bytes)), Json);
        lock (_lock) _w.WriteLine(line);
    }

    public static IEnumerable<CaptureEntry> Read(string path) =>
        File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<CaptureEntry>(l, Json)!);

    public static byte[] Bytes(IEnumerable<CaptureEntry> entries, string dir) =>
        entries.Where(e => e.Dir == dir).SelectMany(e => Convert.FromHexString(e.Hex)).ToArray();

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Dispose() => _w.Dispose();
}

/// <summary>Passes every byte through unchanged and copies it to the capture log.</summary>
public sealed class TapStream(Stream inner, CaptureLog log, string readDir, string writeDir) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        log.Write(readDir, buffer.AsSpan(offset, n));
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct);
        log.Write(readDir, buffer.Span[..n]);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        log.Write(writeDir, buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        log.Write(writeDir, buffer.Span);
        return inner.WriteAsync(buffer, ct);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
