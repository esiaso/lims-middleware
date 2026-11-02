namespace Lims.Instruments.Astm;

/// <summary>Buffered byte reader over a stream with per-read timeouts.</summary>
public sealed class ByteChannel(Stream stream) : IAsyncDisposable
{
    private readonly byte[] _buffer = new byte[4096];
    private int _pos, _len;

    public Stream Stream => stream;

    /// <summary>Returns the next byte, or -1 on timeout. Throws EndOfStreamException when the peer closes.</summary>
    public async Task<int> ReadByteAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_pos < _len) return _buffer[_pos++];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            _len = await stream.ReadAsync(_buffer, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return -1;
        }
        _pos = 0;
        if (_len == 0) throw new EndOfStreamException("link closed by peer");
        return _buffer[_pos++];
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    public Task WriteByteAsync(byte b, CancellationToken ct) => WriteAsync([b], ct);

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
