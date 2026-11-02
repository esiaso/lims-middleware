using System.Text;

namespace Lims.Instruments.Astm;

/// <summary>One E1381 frame: STX FN text ETB|ETX C1 C2 CR LF.</summary>
public sealed record AstmFrame(int FrameNumber, byte[] Text, bool IsFinal)
{
    /// <summary>Sum of bytes from the frame number through ETB/ETX, modulo 256, as two uppercase hex digits.</summary>
    public static string Checksum(ReadOnlySpan<byte> fnThroughTerminator)
    {
        var sum = 0;
        foreach (var b in fnThroughTerminator) sum = (sum + b) & 0xFF;
        return sum.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
    }

    public byte[] ToBytes()
    {
        var body = new byte[Text.Length + 2];
        body[0] = (byte)('0' + FrameNumber);
        Text.CopyTo(body, 1);
        body[^1] = IsFinal ? AstmControl.ETX : AstmControl.ETB;
        var cs = Encoding.ASCII.GetBytes(Checksum(body));
        var frame = new byte[body.Length + 5];
        frame[0] = AstmControl.STX;
        body.CopyTo(frame, 1);
        frame[^4] = cs[0];
        frame[^3] = cs[1];
        frame[^2] = AstmControl.CR;
        frame[^1] = AstmControl.LF;
        return frame;
    }

    /// <summary>
    /// Parses bytes from STX through LF. Returns null with a reason when the frame is malformed
    /// or its checksum is wrong (the caller answers NAK).
    /// </summary>
    public static AstmFrame? TryParse(ReadOnlySpan<byte> raw, out string? error)
    {
        error = null;
        // Minimum: STX FN ETX C1 C2 CR LF
        if (raw.Length < 7) { error = "frame too short"; return null; }
        if (raw.Length > 247) { error = "frame longer than 247 bytes"; return null; }
        if (raw[0] != AstmControl.STX) { error = "missing STX"; return null; }
        if (raw[^2] != AstmControl.CR || raw[^1] != AstmControl.LF) { error = "missing CR LF"; return null; }
        var term = raw[^5];
        if (term != AstmControl.ETX && term != AstmControl.ETB) { error = "missing ETX/ETB"; return null; }
        var fnChar = raw[1];
        if (fnChar is < (byte)'0' or > (byte)'7') { error = "bad frame number"; return null; }
        var expected = Checksum(raw[1..^4]);
        var actual = Encoding.ASCII.GetString(raw[^4..^2]);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) { error = $"checksum {actual} != {expected}"; return null; }
        var text = raw[2..^5].ToArray();
        foreach (var b in text)
        {
            if (b is AstmControl.STX or AstmControl.ETX or AstmControl.EOT or AstmControl.ENQ or AstmControl.ACK
                or AstmControl.NAK or AstmControl.ETB or AstmControl.LF or 0x01 or 0x10 or 0x11 or 0x12 or 0x13 or 0x14 or 0x16)
            { error = "control character inside frame text"; return null; }
        }
        return new AstmFrame(fnChar - '0', text, term == AstmControl.ETX);
    }

    /// <summary>
    /// Splits a message (records separated by CR) into frames: one record per frame, records longer than
    /// 240 characters continue in ETB frames. Frame numbers start at 1 and wrap 7 -> 0.
    /// </summary>
    public static IReadOnlyList<AstmFrame> BuildFrames(string message, Encoding? encoding = null)
    {
        encoding ??= Encoding.ASCII;
        var frames = new List<AstmFrame>();
        var fn = 1;
        var records = message.Replace("\r\n", "\r", StringComparison.Ordinal).Split('\r', StringSplitOptions.RemoveEmptyEntries);
        foreach (var record in records)
        {
            var bytes = encoding.GetBytes(record + "\r");
            for (var offset = 0; offset < bytes.Length; offset += AstmControl.MaxFrameText)
            {
                var len = Math.Min(AstmControl.MaxFrameText, bytes.Length - offset);
                var isLast = offset + len >= bytes.Length;
                frames.Add(new AstmFrame(fn, bytes.AsSpan(offset, len).ToArray(), isLast));
                fn = (fn + 1) % 8;
            }
        }
        return frames;
    }
}
