using System.Net.Sockets;
using System.Text;

namespace Lims.Clinical;

public sealed record LabelData(string Barcode, string Accession, string PatientName, string Mrn, DateOnly? BirthDate, string SpecimenType, string Tests, DateTimeOffset CollectedAt);

/// <summary>Zebra ZPL accession label with a Code 128 barcode (design 5.1, decision E7). Layout sized for a 50 x 25 mm label at 203 dpi.</summary>
public static class ZplLabel
{
    public static string Render(LabelData d)
    {
        static string Clean(string s) => new(s.Where(c => c >= 0x20 && c < 0x7F && c != '^' && c != '~').ToArray());
        var sb = new StringBuilder();
        sb.Append("^XA^CI28^PW400^LL200^LH0,0\n");
        sb.Append("^FO20,10^A0N,22,22^FD").Append(Clean(Truncate(d.PatientName, 28))).Append("^FS\n");
        sb.Append("^FO20,34^A0N,20,20^FDMRN ").Append(Clean(d.Mrn)).Append(d.BirthDate is { } b ? $"  DOB {b:yyyy-MM-dd}" : "").Append("^FS\n");
        sb.Append("^FO20,58^BY2,2,70^BCN,70,N,N,N^FD").Append(Clean(d.Barcode)).Append("^FS\n");
        sb.Append("^FO20,134^A0N,24,24^FD").Append(Clean(d.Accession)).Append("^FS\n");
        sb.Append("^FO20,162^A0N,18,18^FD").Append(Clean(Truncate($"{d.SpecimenType} {d.Tests}", 34))).Append("^FS\n");
        sb.Append("^FO250,134^A0N,18,18^FD").Append(d.CollectedAt.ToString("dd-MM HH:mm", System.Globalization.CultureInfo.InvariantCulture)).Append("^FS\n");
        sb.Append("^XZ\n");
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Sends ZPL to a Zebra printer on raw TCP port 9100.</summary>
    public static async Task PrintAsync(string host, int port, string zpl, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(host, port, cts.Token);
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(zpl), cts.Token);
    }
}
