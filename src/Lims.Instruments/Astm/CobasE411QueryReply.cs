using Lims.Contracts;

namespace Lims.Instruments.Astm;

/// <summary>
/// Builds the host's answer to a real-time query (TSREQ^REAL), following manual section F
/// "Test selection information in real time". Sequence, carrier and position are echoed unchanged.
/// </summary>
public static class CobasE411QueryReply
{
    public const int MaxTestsPerOrder = 18;

    public static string Build(E411ProtocolVariant variant, NormalizedInstrumentMessage query, InstrumentOrder? order)
    {
        var q = query.Query ?? throw new ArgumentException("not a query message", nameof(query));
        var sid = query.SampleId ?? "";
        var tests = order is null
            ? ""
            : string.Join("\\", order.InstrumentTestCodes.Distinct().Take(MaxTestsPerOrder).Select(c => $"^^^{c}^"));
        var priority = order?.Priority == "stat" ? "S" : "R";

        var o = new string[26];
        Array.Fill(o, "");
        o[0] = "O";
        o[1] = "1";
        o[2] = sid;
        o[4] = tests;
        o[5] = priority;

        if (variant == E411ProtocolVariant.Cobas)
        {
            var sampleType = q.SampleType ?? "S1";
            o[3] = $"{q.SequenceNumber}^{q.CarrierNumber}^{q.PositionNumber}^^{sampleType}^{q.ContainerType ?? "SC"}";
            o[11] = "A";
            o[15] = sampleType.Length == 2 && char.IsAsciiDigit(sampleType[1]) ? sampleType[1..] : "1";
            o[25] = "O";
            return string.Join("\r",
                "H|\\^&|||host^1|||||cobas-e411|TSDWN^REPLY|P|1",
                "P|1",
                string.Join("|", o),
                "L|1|N") + "\r";
        }

        o[3] = $"{q.SequenceNumber}^{q.CarrierNumber}^{q.PositionNumber}^^{q.SampleType ?? "SAMPLE"}^{q.ContainerType ?? "NORMAL"}";
        o[11] = "N";
        o[25] = order is null ? "Z" : "Q";
        return string.Join("\r",
            "H|\\^&||||||||||P||",
            "P|1",
            string.Join("|", o),
            "L|1|") + "\r";
    }
}
