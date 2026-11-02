using Lims.Contracts;
using Lims.Instruments.Astm;

namespace Lims.UnitTests;

public class QueryReplyTests
{
    private static NormalizedInstrumentMessage Q(string sid, string type, string container) =>
        new("e411-1", MessageKind.Query, sid, [], "c", Query: new QueryDetails("40", "0", "5", type, container, false));

    [Fact]
    public void Cobas_reply_matches_manual_example_1()
    {
        var reply = CobasE411QueryReply.Build(E411ProtocolVariant.Cobas, Q("000004", "S1", "SC"), new InstrumentOrder("000004", "routine", ["10", "30", "40"]));
        var lines = reply.TrimEnd('\r').Split('\r');
        Assert.Equal("H|\\^&|||host^1|||||cobas-e411|TSDWN^REPLY|P|1", lines[0]);
        Assert.Equal("P|1", lines[1]);
        // Manual: O|1|000004|40^0^5^^S1^SC|^^^10^\^^^30^2\^^^40^|R||||||A||||1||||||||||O (we order without dilution)
        Assert.Equal("O|1|000004|40^0^5^^S1^SC|^^^10^\\^^^30^\\^^^40^|R||||||A||||1||||||||||O", lines[2]);
        Assert.Equal("L|1|N", lines[3]);
    }

    [Fact]
    public void Cobas_reply_without_order_matches_manual_example_2()
    {
        var reply = CobasE411QueryReply.Build(E411ProtocolVariant.Cobas, Q("000004", "S1", "SC"), null);
        Assert.Contains("O|1|000004|40^0^5^^S1^SC||R||||||A||||1||||||||||O", reply);
    }

    [Fact]
    public void Elecsys_replies_use_Q_and_Z_report_types()
    {
        var withOrder = CobasE411QueryReply.Build(E411ProtocolVariant.Elecsys, Q("000004", "SAMPLE", "NORMAL"), new InstrumentOrder("000004", "stat", ["10"]));
        Assert.Contains("O|1|000004|40^0^5^^SAMPLE^NORMAL|^^^10^|S||||||N||||||||||||||Q", withOrder);
        var none = CobasE411QueryReply.Build(E411ProtocolVariant.Elecsys, Q("000004", "SAMPLE", "NORMAL"), null);
        Assert.Contains("O|1|000004|40^0^5^^SAMPLE^NORMAL||R||||||N||||||||||||||Z", none);
    }

    [Fact]
    public void At_most_18_tests_and_no_repeats()
    {
        var codes = Enumerable.Range(1, 25).Select(i => i.ToString()).Append("1").ToList();
        var reply = CobasE411QueryReply.Build(E411ProtocolVariant.Cobas, Q("1", "S1", "SC"), new InstrumentOrder("1", "routine", codes));
        var o = reply.Split('\r')[2].Split('|')[4];
        Assert.Equal(18, o.Split('\\').Length);
    }
}
