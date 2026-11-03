namespace Lims.Instruments.Hl7;

/// <summary>Minimal Lower Layer Protocol framing: VT data FS CR (HL7 2.3.1 appendix C, Mindray protocol section 1).</summary>
public static class Mllp
{
    public const byte StartBlock = 0x0B;
    public const byte EndBlock = 0x1C;
    public const byte CarriageReturn = 0x0D;
    public const int MaxMessageBytes = 4 * 1024 * 1024; // histograms arrive as Base64; generous but bounded

    public static byte[] Frame(byte[] payload)
    {
        var buf = new byte[payload.Length + 3];
        buf[0] = StartBlock;
        payload.CopyTo(buf, 1);
        buf[^2] = EndBlock;
        buf[^1] = CarriageReturn;
        return buf;
    }

    /// <summary>
    /// Reads the next framed message. Bytes before a start block are discarded. Returns null on a clean end of stream
    /// between messages; throws on a stream that ends inside a message or a message over the size limit.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var one = new byte[1];
        // Hunt for the start block.
        while (true)
        {
            if (await stream.ReadAsync(one, ct) == 0) return null;
            if (one[0] == StartBlock) break;
        }
        using var ms = new MemoryStream();
        while (true)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException("connection closed inside an MLLP message");
            if (one[0] == EndBlock)
            {
                if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException("connection closed after end block");
                if (one[0] != CarriageReturn) ms.WriteByte(EndBlock); // tolerate a stray FS: keep it as data
                else return ms.ToArray();
                if (one[0] == StartBlock) { ms.SetLength(0); continue; }
                ms.WriteByte(one[0]);
                continue;
            }
            if (one[0] == StartBlock) { ms.SetLength(0); continue; } // restart: previous block was never closed
            ms.WriteByte(one[0]);
            if (ms.Length > MaxMessageBytes) throw new InvalidDataException("MLLP message over size limit");
        }
    }
}
