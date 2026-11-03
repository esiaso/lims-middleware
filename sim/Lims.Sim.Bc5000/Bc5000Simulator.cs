using System.Globalization;
using System.Text;

namespace Lims.Sim.Bc5000;

public sealed record SimParameter(string Code, string Name, string Value, string Units, string Range = "", string Flag = "N", string CodeSystem = "LN");

/// <summary>
/// Plays the Mindray BC-5000 side of the HL7 v2.3.1 / MLLP link, following the "3107 HL7 Communication Protocol"
/// V2.0 examples (section 5). Written independently of the middleware's HL7 code so the two cannot share a bug.
/// ILLUSTRATIVE until replaced by captured traffic.
/// </summary>
public sealed class Bc5000Simulator(Stream stream, TimeSpan? timeout = null)
{
    private const byte VT = 0x0B, FS = 0x1C, CR = 0x0D;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);
    private int _controlId = 1;

    /// <summary>Typical CBC+DIFF parameter set with synthetic values.</summary>
    public static IReadOnlyList<SimParameter> NormalCbc() =>
    [
        new("6690-2", "WBC", "6.45", "10*9/L", "4.00-10.00"),
        new("751-8", "NEU#", "3.80", "10*9/L", "2.00-7.00"),
        new("770-8", "NEU%", "58.9", "%", "50.0-70.0"),
        new("731-0", "LYM#", "2.01", "10*9/L", "0.80-4.00"),
        new("736-9", "LYM%", "31.2", "%", "20.0-40.0"),
        new("789-8", "RBC", "4.62", "10*12/L", "3.50-5.50"),
        new("718-7", "HGB", "138", "g/L", "110-160"),
        new("4544-3", "HCT", "41.3", "%", "37.0-54.0"),
        new("777-3", "PLT", "245", "10*9/L", "100-300"),
    ];

    public string ResultMessage(string sampleId, IEnumerable<SimParameter> parameters, DateTime analysedAt, string testMode = "CBC+DIFF",
        IEnumerable<string>? alerts = null, string patientId = "SIM-0001")
    {
        var id = _controlId++;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"MSH|^~\\&|||||{analysedAt:yyyyMMddHHmmss}||ORU^R01|{id}|P|2.3.1||||||UNICODE\r");
        sb.Append(CultureInfo.InvariantCulture, $"PID|1||{patientId}^^^^MR||^Synthetic||19900101000000|Female\r");
        sb.Append("PV1|1||Lab^^1\r");
        sb.Append(CultureInfo.InvariantCulture, $"OBR|1||{sampleId}|00001^Automated Count^99MRC||{analysedAt:yyyyMMddHHmmss}|{analysedAt:yyyyMMddHHmmss}|||||||{analysedAt:yyyyMMddHHmmss}||||||||||HM||||||||tech1\r");
        var n = 1;
        sb.Append(CultureInfo.InvariantCulture, $"OBX|{n++}|IS|08001^Take Mode^99MRC||O||||||F\r");
        sb.Append(CultureInfo.InvariantCulture, $"OBX|{n++}|IS|08002^Blood Mode^99MRC||W||||||F\r");
        sb.Append(CultureInfo.InvariantCulture, $"OBX|{n++}|IS|08003^Test Mode^99MRC||{testMode}||||||F\r");
        sb.Append(CultureInfo.InvariantCulture, $"OBX|{n++}|NM|30525-0^Age^LN||36|yr|||||F\r");
        foreach (var p in parameters)
            sb.Append(CultureInfo.InvariantCulture, $"OBX|{n++}|NM|{p.Code}^{p.Name}^{p.CodeSystem}||{p.Value}|{p.Units}|{p.Range}|{p.Flag}|||F\r");
        foreach (var a in alerts ?? [])
            sb.Append(CultureInfo.InvariantCulture, $"OBX|{n++}|IS|{a}^99MRC||T||||||F\r");
        sb.Append(CultureInfo.InvariantCulture, $"OBX|{n++}|ED|15000^WBC Histogram. Binary^99MRC||^Application^Octet-stream^Base64^AAAAAAAAAAAAAAAAAAAAAA==||||||F\r");
        return sb.ToString();
    }

    public string QcMessage(string qcFileNo, string lot, IEnumerable<SimParameter> parameters, DateTime at)
    {
        var id = _controlId++;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"MSH|^~\\&|||||{at:yyyyMMddHHmmss}||ORU^R01|{id}|Q|2.3.1||||||UNICODE\r");
        sb.Append(CultureInfo.InvariantCulture, $"PID|1||{lot}^^^^MR||||20271231000000\r");
        sb.Append(CultureInfo.InvariantCulture, $"OBR|1||{qcFileNo}|00003^LJ QCR^99MRC||{at:yyyyMMddHHmmss}|{at:yyyyMMddHHmmss}\r");
        var n = 1;
        sb.Append(CultureInfo.InvariantCulture, $"OBX|{n++}|IS|05001^Qc Level^99MRC||M||||||F\r");
        foreach (var p in parameters)
            sb.Append(CultureInfo.InvariantCulture, $"OBX|{n++}|NM|{p.Code}^{p.Name}^{p.CodeSystem}||{p.Value}|{p.Units}|{p.Range}|{p.Flag}|||F\r");
        return sb.ToString();
    }

    public string WorklistQuery(string sampleId, DateTime at)
    {
        var id = _controlId++;
        return $"MSH|^~\\&|||||{at:yyyyMMddHHmmss}||ORM^O01|{id}|P|2.3.1||||||UNICODE\rORC|RF||{sampleId}||IP\r";
    }

    /// <summary>Sends one message and waits for the host's reply (ACK^R01 or ORR^O02). Null when no reply arrives in time.</summary>
    public async Task<string?> SendAsync(string message, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        var frame = new byte[payload.Length + 3];
        frame[0] = VT;
        payload.CopyTo(frame, 1);
        frame[^2] = FS;
        frame[^1] = CR;
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
        return await ReceiveAsync(ct);
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        var one = new byte[1];
        var buf = new List<byte>();
        var inBlock = false;
        try
        {
            while (true)
            {
                if (await stream.ReadAsync(one, cts.Token) == 0) return null;
                var b = one[0];
                if (!inBlock) { if (b == VT) inBlock = true; continue; }
                if (b == FS)
                {
                    await stream.ReadExactlyAsync(one, cts.Token); // CR
                    return Encoding.UTF8.GetString(buf.ToArray());
                }
                buf.Add(b);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>MSA-1 of a reply ("AA", "AE", "AR"), or null.</summary>
    public static string? AckCode(string? reply)
    {
        var msa = reply?.Split('\r').FirstOrDefault(s => s.StartsWith("MSA|", StringComparison.Ordinal));
        return msa?.Split('|').ElementAtOrDefault(1);
    }
}
