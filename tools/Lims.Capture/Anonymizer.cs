using System.Text;

namespace Lims.Capture;

/// <summary>
/// Removes patient identity from captured messages before they leave the hospital or go into the repository (design 9:
/// no PHI in test data). Sample IDs are replaced consistently across all files of one run (the same tube keeps the
/// same pseudonym), so query, order and result messages still match. Values, units, flags, test codes, instrument
/// names and timestamps are kept, because the parser tests need them.
/// </summary>
public sealed class Anonymizer
{
    private readonly Dictionary<string, string> _samples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _patients = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> SampleMap => _samples;

    private string Sample(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.StartsWith('@')) return id; // "@n" = unreadable barcode marker (e411)
        if (!_samples.TryGetValue(id, out var p)) _samples[id] = p = $"ANON{_samples.Count + 1:D6}";
        return p;
    }

    private string Patient(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return id;
        if (!_patients.TryGetValue(id, out var p)) _patients[id] = p = $"PAT{_patients.Count + 1:D6}";
        return p;
    }

    // ------------------------------------------------------------------ ASTM (e411, c111)

    public string Astm(string message)
    {
        var records = message.Split('\r');
        var fd = '|'; var cd = '^';
        for (var r = 0; r < records.Length; r++)
        {
            var rec = records[r];
            if (rec.Length < 2) continue;
            if (rec[0] == 'H' && rec.Length > 5) { fd = rec[1]; cd = rec[3]; continue; }
            var f = rec.Split(fd);
            switch (char.ToUpperInvariant(rec[0]))
            {
                case 'P':
                    // Keep type and sequence only: practice/lab patient IDs, name, birth date, address, physician, location.
                    for (var i = 2; i < f.Length; i++) f[i] = "";
                    break;
                case 'O':
                    MapComponent(f, 3, 0, cd, Sample);           // specimen ID (e411)
                    MapComponent(f, 4, 0, cd, Sample);           // instrument specimen ID (c111 "SID^^pos")
                    for (var i = 16; i <= 20 && i < f.Length; i++) f[i] = ""; // O-17..O-21: physician, phone, user and lab fields
                    break;
                case 'Q':
                    if (f.Length > 2)
                    {
                        var c = f[2].Split(cd);
                        // c111: ^SampleID; e411: ^^SampleID^seq^... A bare "dummy" (c111 cancel) has no components and is kept.
                        if (c.Length > 1 && c[1].Length > 0) c[1] = Sample(c[1]);
                        else if (c.Length > 2 && c[2].Length > 0) c[2] = Sample(c[2]);
                        f[2] = string.Join(cd, c);
                    }
                    break;
                case 'R':
                    if (f.Length > 10 && f[10].Length > 0 && !f[10].StartsWith('$')) f[10] = "operator"; // operator ID
                    break;
                case 'C':
                    break; // instrument alarm codes, no identity
            }
            records[r] = string.Join(fd, f);
        }
        return string.Join('\r', records);
    }

    private static void MapComponent(string[] f, int astmField, int comp, char cd, Func<string, string> map)
    {
        var i = astmField - 1;
        if (i >= f.Length || f[i].Length == 0) return;
        var c = f[i].Split(cd);
        if (comp < c.Length && c[comp].Length > 0) c[comp] = map(c[comp]);
        f[i] = string.Join(cd, c);
    }

    // ------------------------------------------------------------------ HL7 (BC-5000)

    public string Hl7(string message)
    {
        var segs = message.Replace("\r\n", "\r").Split('\r');
        if (segs.Length == 0 || !segs[0].StartsWith("MSH", StringComparison.Ordinal) || segs[0].Length < 8) return message;
        var fd = segs[0][3]; var cd = segs[0][4];
        var msh = segs[0].Split(fd);
        var isQc = msh.Length > 10 && msh[10].Trim() == "Q";
        for (var s = 0; s < segs.Length; s++)
        {
            var seg = segs[s];
            if (seg.Length < 3) continue;
            var f = seg.Split(fd);
            switch (seg[..3])
            {
                case "PID":
                    // QC messages carry the control lot in PID-3 (protocol 4.3.3): keep it. Otherwise pseudonymize the MRN.
                    if (!isQc && f.Length > 3) f[3] = string.Join(cd, f[3].Split(cd).Select((c, i) => i == 0 ? Patient(c) : c));
                    for (var i = 2; i < f.Length; i++)
                    {
                        if (i == 3) continue;
                        if (isQc && i == 7) continue;            // QC: expiry date of the control
                        if (i == 8) continue;                     // sex is used by reference ranges on the analyzer
                        f[i] = i == 7 && f[i].Length >= 4 ? f[i][..4] + "0101" : ""; // birth date: keep the year only
                    }
                    break;
                case "PV1":
                    for (var i = 2; i < f.Length; i++) f[i] = "";
                    break;
                case "OBR":
                    MapField(f, 2, cd, Sample);
                    MapField(f, 3, cd, Sample);
                    if (f.Length > 32 && f[32].Length > 0) f[32] = "operator";
                    for (var i = 10; i < Math.Min(f.Length, 32); i++) if (i is 10 or 16 or 28) f[i] = ""; // collector, ordering provider, copies to
                    break;
                case "ORC":
                    MapField(f, 2, cd, Sample);
                    MapField(f, 3, cd, Sample);
                    break;
                case "OBX":
                    // Free-text items (remarks) may hold a name: blank them, keep every coded and numeric item.
                    if (f.Length > 5 && f[2].Trim() is "ST" or "FT" or "TX") f[5] = "";
                    break;
            }
            segs[s] = string.Join(fd, f);
        }
        return string.Join('\r', segs);
    }

    private static void MapField(string[] f, int hl7Field, char cd, Func<string, string> map)
    {
        if (hl7Field >= f.Length || f[hl7Field].Length == 0) return;
        var c = f[hl7Field].Split(cd);
        c[0] = map(c[0]);
        f[hl7Field] = string.Join(cd, c);
    }

    public byte[] Apply(string protocol, byte[] raw) => protocol == "hl7"
        ? Encoding.UTF8.GetBytes(Hl7(Encoding.UTF8.GetString(raw)))
        : Encoding.Latin1.GetBytes(Astm(Encoding.Latin1.GetString(raw)));
}
