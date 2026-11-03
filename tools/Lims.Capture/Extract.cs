using System.Text;
using Lims.Instruments.Astm;
using Lims.Instruments.Hl7;

namespace Lims.Capture;

/// <summary>Rebuilds complete messages from one direction of a capture log (needed for proxy captures).</summary>
public static class Extract
{
    /// <summary>
    /// ASTM E1381: frames are STX..CR LF. Bad checksums and repeated frame numbers (the sender retransmitting after a lost
    /// ACK) are skipped the same way the receiver would; a message ends with its L record. EOT drops an unfinished message.
    /// </summary>
    public static List<byte[]> Astm(byte[] stream)
    {
        var messages = new List<byte[]>();
        var current = new List<byte>();
        var lastFn = -1;
        for (var i = 0; i < stream.Length; i++)
        {
            if (stream[i] == AstmControl.EOT) { current.Clear(); lastFn = -1; continue; }
            if (stream[i] != AstmControl.STX) continue;
            var end = i + 1;
            while (end < stream.Length - 1 && !(stream[end] == AstmControl.CR && stream[end + 1] == AstmControl.LF)) end++;
            if (end >= stream.Length - 1) break;
            var frame = AstmFrame.TryParse(stream.AsSpan(i, end + 2 - i), out _);
            i = end + 1;
            if (frame is null || frame.FrameNumber == lastFn) continue;
            lastFn = frame.FrameNumber;
            current.AddRange(frame.Text);
            if (frame.IsFinal && (current.Count == 0 || current[^1] != AstmControl.CR)) current.Add(AstmControl.CR);
            if (frame.IsFinal && EndsWithTerminator(current))
            {
                messages.Add(current.ToArray());
                current.Clear();
            }
        }
        return messages;
    }

    private static bool EndsWithTerminator(List<byte> m)
    {
        var text = Encoding.ASCII.GetString(m.ToArray()).TrimEnd('\r');
        var last = text[(text.LastIndexOf('\r') + 1)..];
        return last.StartsWith("L|", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<List<byte[]>> Hl7Async(byte[] stream)
    {
        var messages = new List<byte[]>();
        using var ms = new MemoryStream(stream);
        while (true)
        {
            byte[]? m;
            try { m = await Mllp.ReadAsync(ms, default); }
            catch (EndOfStreamException) { break; } // capture stopped inside a message
            if (m is null) break;
            messages.Add(m);
        }
        return messages;
    }
}
