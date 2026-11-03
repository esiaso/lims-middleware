using System.Globalization;
using System.Text;

namespace Lims.Sim.E411;

/// <summary>
/// cobas c 111 message builders following the c111 Host Interface Manual v2.2 examples (section 9).
/// The link layer is the same ASTM E1381 as the e411, so <see cref="E411Simulator"/> sends these too.
/// Written independently of the middleware's parser. ILLUSTRATIVE until replaced by captured traffic.
/// </summary>
public static class C111Messages
{
    private const string Header = "H|\\^&|||c111^Roche^c111^3.0.0.0710^1^333444|||||host|";

    /// <summary>Result upload (manual 9.9): one O record per sample, R + C (flags) records per test, one record per frame.</summary>
    public static string Results(string sampleId, IEnumerable<SimResult> results, DateTime reportedAt, string mode = "RSUPL^BATCH", string? refRange = null, int position = 3)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{Header}{mode}|P|1|{reportedAt:yyyyMMddHHmmss}\r");
        sb.Append("P|1||\r");
        sb.Append(CultureInfo.InvariantCulture, $"O|1||{sampleId}^^{position}||R||||||N|||||||||||{reportedAt:yyyyMMddHHmmss}|||F\r");
        var i = 1;
        foreach (var r in results)
        {
            sb.Append(CultureInfo.InvariantCulture, $"R|{i}|^^^{r.TestCode}|{r.Value}|{r.Units}|{refRange}|{r.Flag}||{r.Status}||admin\r");
            if (r.AlarmCode is not null) sb.Append(CultureInfo.InvariantCulture, $"C|{i}|I|{r.AlarmCode}|I\r");
            i++;
        }
        sb.Append("L|1|N\r");
        return sb.ToString();
    }

    /// <summary>QC upload: action code Q, sample ID "1000 + control code" (manual 7.2.2.5).</summary>
    public static string Qc(int controlCode, string testCode, string value, string units, DateTime reportedAt, string status = "F") =>
        $"{Header}RSUPL^REAL|P|1|{reportedAt:yyyyMMddHHmmss}\r" +
        "P|1||\r" +
        $"O|1||{1000 + controlCode}^^1||R||||||Q|||||||||||{reportedAt:yyyyMMddHHmmss}|||F\r" +
        $"R|1|^^^{testCode}|{value}|{units}|20.0\\30.0|N||{status}||admin\r" +
        "L|1|N\r";

    /// <summary>Order query (manual 9.1).</summary>
    public static string Query(string sampleId, DateTime at) =>
        $"{Header}TSREQ^REAL|P|1|{at:yyyyMMddHHmmss}\rQ|1|^{sampleId}||ALL||||||||O\rL|1|N\r";

    /// <summary>Calibration upload: not a patient or QC result.</summary>
    public static string Calibration(DateTime at) =>
        $"{Header}PCUPL^REAL|P|1|{at:yyyyMMddHHmmss}\rL|1|N\r";
}
