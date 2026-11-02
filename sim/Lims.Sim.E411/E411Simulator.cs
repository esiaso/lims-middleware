using System.Globalization;
using System.Text;

namespace Lims.Sim.E411;

public sealed record SimResult(string TestCode, string Value, string Units, string Flag = "N", string Status = "F", string? AlarmCode = null);

public sealed class SendOptions
{
    /// <summary>Corrupt the checksum of this 0-based frame on its first transmission.</summary>
    public int? CorruptChecksumFrame { get; set; }
    /// <summary>Send this 0-based frame twice (simulates a lost ACK).</summary>
    public int? DuplicateFrame { get; set; }
    /// <summary>Stop after this many frames without EOT (simulates a cable pull).</summary>
    public int? AbortAfterFrames { get; set; }
}

public enum SendStatus { Accepted, Refused, Failed }

/// <summary>
/// Plays the analyzer side of the cobas e 411 host link (ASTM E1381/E1394, "cobas type" by default),
/// using the record layouts in Host Interface Manual v2.2 section F. Independent of the middleware's
/// own link code so the two cannot share a bug. ILLUSTRATIVE until replaced by captured traffic.
/// </summary>
public sealed class E411Simulator(Stream stream, TimeSpan? timeout = null)
{
    private const byte STX = 2, ETX = 3, EOT = 4, ENQ = 5, ACK = 6, NAK = 0x15, ETB = 0x17;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);
    private readonly byte[] _one = new byte[1];

    public int NaksReceived { get; private set; }

    // ------------------------------------------------------------ message builders (manual section F)

    public static string ResultMessage(string sampleId, IEnumerable<SimResult> results, DateTime completedAt, int sequence = 40, string carrier = "0", int position = 5)
    {
        var sb = new StringBuilder();
        sb.Append("H|\\^&|||cobas-e411^1|||||host|RSUPL^REAL|P|1\r");
        sb.Append("P|1\r");
        var list = results.ToList();
        var tests = string.Join("\\", list.Select(r => $"^^^{r.TestCode}^"));
        sb.Append(CultureInfo.InvariantCulture, $"O|1|{sampleId}|{sequence}^{carrier}^{position}^^S1^SC|{tests}|R||||||N||||1|||||||{completedAt:yyyyMMddHHmmss}|||F\r");
        var i = 1;
        foreach (var r in list)
        {
            sb.Append(CultureInfo.InvariantCulture, $"R|{i++}|^^^{r.TestCode}//not|{r.Value}^|{r.Units}||{r.Flag}||{r.Status}||admin|||E1\r");
            if (r.AlarmCode is not null) sb.Append(CultureInfo.InvariantCulture, $"C|1|I|{r.AlarmCode}|I\r");
        }
        sb.Append("L|1|N\r");
        return sb.ToString();
    }

    public static string QcMessage(string controlName, string lot, string testCode, string value, string units, DateTime completedAt)
    {
        return "H|\\^&|||cobas-e411^1|||||host|RSUPL^REAL|P|1\r" +
               "P|1\r" +
               $"O|1|{controlName}^{lot}|96^0019^1^^QC^SC|^^^{testCode}^|R||||||Q||||1|||||||{completedAt:yyyyMMddHHmmss}|||F\r" +
               $"R|1|^^^{testCode}//not|{value}^|{units}||N||F||admin|||E1\r" +
               "L|1|N\r";
    }

    public static string QueryMessage(string sampleId, int sequence = 40, string carrier = "0", int position = 5, bool cancel = false) =>
        "H|\\^&|||cobas-e411^1|||||host|TSREQ^REAL|P|1\r" +
        $"Q|1|^^{sampleId}^{sequence}^{carrier}^{position}^^S1^SC||ALL||||||||{(cancel ? "A" : "O")}\r" +
        "L|1|N\r";

    // ------------------------------------------------------------ link layer

    public static byte[] Frame(int fn, string text, bool final, bool corrupt = false)
    {
        var body = new List<byte> { (byte)('0' + fn) };
        body.AddRange(Encoding.ASCII.GetBytes(text));
        body.Add(final ? ETX : ETB);
        var sum = body.Aggregate(0, (a, b) => (a + b) & 0xFF);
        if (corrupt) sum = (sum + 1) & 0xFF;
        var frame = new List<byte> { STX };
        frame.AddRange(body);
        frame.AddRange(Encoding.ASCII.GetBytes(sum.ToString("X2", CultureInfo.InvariantCulture)));
        frame.Add(0x0D);
        frame.Add(0x0A);
        return frame.ToArray();
    }

    public static IReadOnlyList<(string Text, bool Final)> SplitFrames(string message)
    {
        var frames = new List<(string, bool)>();
        foreach (var rec in message.Split('\r', StringSplitOptions.RemoveEmptyEntries))
        {
            var r = rec + "\r";
            for (var i = 0; i < r.Length; i += 240)
                frames.Add((r.Substring(i, Math.Min(240, r.Length - i)), i + 240 >= r.Length));
        }
        return frames;
    }

    public async Task<SendStatus> SendAsync(string message, SendOptions? opt = null, CancellationToken ct = default)
    {
        opt ??= new SendOptions();
        await WriteAsync([ENQ], ct);
        var reply = await ReadAsync(ct);
        if (reply == NAK) return SendStatus.Refused;
        if (reply != ACK) return SendStatus.Failed;

        var frames = SplitFrames(message);
        var fn = 1;
        for (var i = 0; i < frames.Count; i++)
        {
            if (opt.AbortAfterFrames is { } stop && i >= stop) return SendStatus.Failed;
            var corruptOnce = opt.CorruptChecksumFrame == i;
            var attempts = 0;
            while (true)
            {
                attempts++;
                await WriteAsync(Frame(fn, frames[i].Text, frames[i].Final, corruptOnce && attempts == 1), ct);
                var r = await ReadAsync(ct);
                if (r == ACK) break;
                if (r == NAK) { NaksReceived++; if (attempts < 6) continue; }
                await WriteAsync([EOT], ct);
                return SendStatus.Failed;
            }
            if (opt.DuplicateFrame == i)
            {
                await WriteAsync(Frame(fn, frames[i].Text, frames[i].Final), ct);
                if (await ReadAsync(ct) != ACK) return SendStatus.Failed;
            }
            fn = (fn + 1) % 8;
        }
        await WriteAsync([EOT], ct);
        return SendStatus.Accepted;
    }

    /// <summary>Waits for the host to send a message (ENQ ... EOT) and returns the de-framed text, or null on timeout.</summary>
    public async Task<string?> ReceiveAsync(CancellationToken ct = default)
    {
        int b;
        do { b = await ReadAsync(ct); if (b < 0) return null; } while (b != ENQ);
        await WriteAsync([ACK], ct);
        var text = new StringBuilder();
        while (true)
        {
            b = await ReadAsync(ct);
            if (b < 0) return null;
            if (b == EOT) return text.ToString();
            if (b != STX) continue;
            var frame = new List<byte> { STX };
            while (true)
            {
                var c = await ReadAsync(ct);
                if (c < 0) return null;
                frame.Add((byte)c);
                if (c == 0x0A) break;
            }
            var body = frame.Skip(1).Take(frame.Count - 5).ToList();
            var sum = body.Aggregate(0, (a, x) => (a + x) & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
            var got = Encoding.ASCII.GetString(frame.Skip(frame.Count - 4).Take(2).ToArray());
            if (sum != got) { await WriteAsync([NAK], ct); continue; }
            text.Append(Encoding.ASCII.GetString(body.Skip(1).Take(body.Count - 2).ToArray()));
            await WriteAsync([ACK], ct);
        }
    }

    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    private async Task<int> ReadAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            var n = await stream.ReadAsync(_one, cts.Token);
            return n == 0 ? -1 : _one[0];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return -1; }
    }
}
