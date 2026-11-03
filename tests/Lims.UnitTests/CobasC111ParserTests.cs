using System.Text;
using Lims.Contracts;
using Lims.Instruments.Astm;

namespace Lims.UnitTests;

/// <summary>Messages below follow the traces in cobas c 111 Host Interface Manual v2.2 section 9, with frame breaks removed.</summary>
public class CobasC111ParserTests
{
    private static readonly CobasC111Parser Parser = new(TimeSpan.FromHours(3));

    private static ParseOutcome Parse(params string[] records) =>
        Parser.Parse("c111-1", Encoding.ASCII.GetBytes(string.Join("\r", records) + "\r"), "corr-1");

    private const string Header = "H|\\^&|||c111^Roche^c111^2.0.0.0710^1^333444|||||host|RSUPL^BATCH|P|1|20071210091358";

    [Fact]
    public void Manual_9_9_result_upload()
    {
        var r = Parse(
            Header,
            "P|1||",
            "O|1||TEST^^3||R||||||N|||||||||||20071210091358|||F",
            "R|1|^^^989|151.1|mmol/L|136.0\\145.0|H||F||admin",
            "C|1|I|40^>RR|I",
            "R|2|^^^990|6.62|mmol/L|3.50\\5.10|H||F||admin",
            "C|2|I|40^>RR|I",
            "R|3|^^^991|118.5|mmol/L|98.0\\107.0|H||F||admin",
            "C|3|I|40^>RR|I",
            "L|1|N");
        Assert.True(r.Success, r.Error);
        var m = Assert.Single(r.Messages);
        Assert.Equal(MessageKind.PatientResult, m.Kind);
        Assert.Equal("TEST", m.SampleId); // sample ID comes from O-4 on the c111
        Assert.Equal(3, m.Results.Count);
        var na = m.Results[0];
        Assert.Equal("989", na.InstrumentTestCode);
        Assert.Equal(151.1m, na.NumericValue);
        Assert.Equal("mmol/L", na.Units);
        Assert.Equal("136.0-145.0", na.ReferenceRangeText);
        Assert.Equal(new[] { "H", "alarm:40" }, na.InstrumentFlags);
        Assert.Equal("F", na.ResultStatus);
        Assert.Equal("admin", na.OperatorId);
        Assert.Equal(new DateTimeOffset(2007, 12, 10, 9, 13, 58, TimeSpan.FromHours(3)), na.RunAt);
    }

    [Fact]
    public void Manual_9_6_query_reply_results_with_raw_data_records()
    {
        var r = Parse(
            "H|\\^&|||c111^Roche^c111^2.0.0.0710^1^333444|||||host|RSUPL^REPLY|P|1|20071210085737",
            "P|1||",
            "O|1||83720||R||||||N|||||||||||20071210085737|||F\\Q",
            "R|1|^^^685|16.69|U/L||N||F||$SYS$",
            "C|1|I||I",
            "M|1|RR^BM^c111^1|-4|-4\\-4\\9|0.002674",
            "R|2|^^^687|13.20|U/L||N||F||$SYS$",
            "C|2|I||I",
            "L|1|N");
        var m = Assert.Single(r.Messages);
        Assert.Equal("83720", m.SampleId);
        Assert.Equal(new[] { "685", "687" }, m.Results.Select(x => x.InstrumentTestCode));
        Assert.Equal(new[] { "N" }, m.Results[0].InstrumentFlags); // empty C record adds nothing
    }

    [Fact]
    public void Statuses_X_R_C_are_mapped()
    {
        var r = Parse(
            Header, "P|1||",
            "O|1||S1^^2||R||||||N|||||||||||20071210091358|||F",
            "R|1|^^^989|151.1|mmol/L||N||X||admin",
            "R|2|^^^990|4.10|mmol/L||N||R||admin",
            "R|3|^^^991|101.0|mmol/L||N||C||admin",
            "L|1|N");
        var res = Assert.Single(r.Messages).Results;
        Assert.Equal("", res[0].RawValue);
        Assert.Null(res[0].NumericValue);
        Assert.Contains("not completed", res[0].InstrumentFlags);
        Assert.Equal("F", res[1].ResultStatus);
        Assert.Contains("retransmission", res[1].InstrumentFlags);
        Assert.True(res[2].IsRerun);
    }

    [Fact]
    public void Qc_order_is_qc_and_ignored_results_are_dropped()
    {
        var r = Parse(
            Header, "P|1||",
            "O|1||1300^^1||R||||||Q|||||||||||20071210091358|||F",
            "R|1|^^^989|140.2|mmol/L||N||F||admin",
            "R|2|^^^990|4.0|mmol/L||N||I||admin",
            "L|1|N");
        var m = Assert.Single(r.Messages);
        Assert.Equal(MessageKind.Qc, m.Kind);
        Assert.Equal("1300", m.ControlName);
        Assert.Equal("989", Assert.Single(m.Results).InstrumentTestCode);
    }

    [Fact]
    public void Order_query_and_cancel()
    {
        var q = Parse("H|\\^&|||c111^Roche^c111^2.0^1^3334|||||host|TSREQ^REAL|P|1", "Q|1|^S123||ALL||||||||O", "L|1|N");
        var m = Assert.Single(q.Messages);
        Assert.Equal(MessageKind.Query, m.Kind);
        Assert.Equal("S123", m.SampleId);
        Assert.False(m.Query!.IsCancel);

        var c = Parse("H|\\^&|||c111^Roche^c111^2.0^1^3334|||||host|TSREQ^REAL|P|1", "Q|1|dummy||||||||||A", "L|1|N");
        Assert.True(Assert.Single(c.Messages).Query!.IsCancel);
    }

    [Fact]
    public void Calibration_upload_is_not_a_result()
    {
        var r = Parse("H|\\^&|||c111^Roche^c111^2.0^1^3334|||||host|PCUPL^BATCH|P|1", "L|1|N");
        Assert.Equal(MessageKind.Calibration, Assert.Single(r.Messages).Kind);
    }

    [Fact]
    public void Order_upload_reply_carries_no_results()
    {
        var r = Parse("H|\\^&|||c111^Roche^c111^2.0^1^3334|||||host|ORUPL^REPLY|P|1", "P|1||",
            "O|1||S9^^1|^^^989\\^^^990|R||||||N|||||||||||20071210091358|||O", "L|1|N");
        Assert.False(r.Success); // nothing to file: quarantined as "no query or order records"
    }

    [Fact]
    public void Query_reply_lists_codes_or_Z_when_no_order()
    {
        var query = new NormalizedInstrumentMessage("c111-1", MessageKind.Query, "S123", [], "c");
        var now = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        var withOrder = CobasC111QueryReply.Build(query, new InstrumentOrder("S123", "stat", ["989", "990", "989"]), now);
        var o = withOrder.Split('\r')[2].Split('|');
        Assert.Equal("S123", o[2]);
        Assert.Equal("^^^989^\\^^^990^", o[4]);
        Assert.Equal("S", o[5]);
        Assert.Equal("A", o[11]);
        Assert.Equal("O\\Q", o[25]);
        Assert.Contains("TSDWN^REPLY", withOrder);

        var none = CobasC111QueryReply.Build(query, null, now);
        Assert.Equal("Z", none.Split('\r')[2].Split('|')[25]);
        Assert.EndsWith("L|1|N\r", none);
    }

    [Fact]
    public void Query_reply_caps_tests_at_60()
    {
        var query = new NormalizedInstrumentMessage("c111-1", MessageKind.Query, "S1", [], "c");
        var codes = Enumerable.Range(1, 75).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var reply = CobasC111QueryReply.Build(query, new InstrumentOrder("S1", "routine", codes), DateTimeOffset.UnixEpoch);
        Assert.Equal(60, reply.Split('\r')[2].Split('|')[4].Split('\\').Length);
    }
}
