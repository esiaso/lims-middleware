using System.Text;
using Lims.Contracts;
using Lims.Instruments.Astm;

namespace Lims.UnitTests;

/// <summary>Messages below are the traces from cobas e 411 Host Interface Manual v2.2 section F, with PDF spacing removed.</summary>
public class CobasE411ParserTests
{
    private static readonly CobasE411Parser Parser = new(TimeSpan.FromHours(3));

    private static ParseOutcome Parse(params string[] records) =>
        Parser.Parse("e411-1", Encoding.ASCII.GetBytes(string.Join("\r", records) + "\r"), "corr-1");

    [Fact]
    public void Cobas_type_real_time_results()
    {
        var r = Parse(
            "H|\\^&|||cobas-e411^1|||||host|RSUPL^REAL|P|1",
            "P|1",
            "O|1|000004|40^0^5^^S1^SC|^^^10^\\^^^30^2\\^^^40^|R||||||N||||1|||||||20051220095504|||F",
            "R|1|^^^10//not|1.25^|ulU/ml||N||F||admin|||E1",
            "R|2|^^^30/2/pre-diluted|0.091^|ng/dl||N||F||admin|||E1",
            "R|3|^^^40//not|1.17^|ng/ml||N||F||admin|||E1",
            "L|1|N");
        Assert.True(r.Success, r.Error);
        var m = Assert.Single(r.Messages);
        Assert.Equal(MessageKind.PatientResult, m.Kind);
        Assert.Equal("000004", m.SampleId);
        Assert.Equal(3, m.Results.Count);
        Assert.Equal("10", m.Results[0].InstrumentTestCode);
        Assert.Equal(1.25m, m.Results[0].NumericValue);
        Assert.Equal("ulU/ml", m.Results[0].Units);
        Assert.Equal("F", m.Results[0].ResultStatus);
        Assert.Equal("admin", m.Results[0].OperatorId);
        Assert.Equal(new DateTimeOffset(2005, 12, 20, 9, 55, 4, TimeSpan.FromHours(3)), m.Results[0].RunAt);
        Assert.Equal("2", m.Results[1].Dilution);
        Assert.Contains("pre-diluted", m.Results[1].InstrumentFlags);
        Assert.Contains("N", m.Results[0].InstrumentFlags);
    }

    [Fact]
    public void Cobas_type_low_result_with_comment_alarm()
    {
        var r = Parse(
            "H|\\^&|||cobas-e411^1|||||host|RSUPL^REAL|P|1",
            "P|1",
            "O|1|000002|3^0007^2^^S1^SC|^^^10^|R||||||N||||1|||||||20051220104418|||F",
            "R|1|^^^10//not|0.163|ulU/ml||L||F||admin|||E1",
            "C|1|I|48|I",
            "L|1|N");
        var res = Assert.Single(Assert.Single(r.Messages).Results);
        Assert.Equal(0.163m, res.NumericValue);
        Assert.Equal(new[] { "L", "alarm:48" }, res.InstrumentFlags);
    }

    [Fact]
    public void Cobas_type_qualitative_keeps_value_and_cut_off_index()
    {
        var r = Parse(
            "H|\\^&|||cobas-e411^1|||||host|RSUPL^REAL|P|1",
            "P|1",
            "O|1|000010|442^0005^1^^S1^SC|^^^400^|R||||||N||||1|||||||20051220104418|||F",
            "R|1|^^^400//not|-1^0.303|COI||N||F||admin|||E1",
            "L|1|N");
        var res = Assert.Single(Assert.Single(r.Messages).Results);
        Assert.Equal("-1", res.QualitativeValue);
        Assert.Equal("0.303", res.SecondaryValue);
        Assert.Equal("400", res.InstrumentTestCode);
    }

    [Fact]
    public void Cobas_type_control_sample_is_qc()
    {
        var r = Parse(
            "H|\\^&|||cobas-e411^1|||||host|RSUPL^REAL|P|1",
            "P|1",
            "O|1|PCU2|96^0019^1^^QC^SC|^^^400^|R||||||Q||||1|||||||20051220104418|||F",
            "R|1|^^^400//not|1.26^|ulU/ml||L||F||admin|||E1",
            "L|1|N");
        var m = Assert.Single(r.Messages);
        Assert.Equal(MessageKind.Qc, m.Kind);
        Assert.Equal("PCU2", m.ControlName);
    }

    [Fact]
    public void Elecsys_type_results_and_control()
    {
        var r = Parse(
            "H|\\^&||||||||||P||",
            "P|1||||||||||||||||||||||||||||||||||",
            "O|1|000004|40^0^5^^SAMPLE^NORMAL|ALL|R|20051220095504|||||X||||||||||||||O",
            "R|1|^^^10^^0|1.25|ulU/ml|0.270^4.20|N||F|||20051220095534|20051220101604|",
            "R|2|^^^30^2^1|>1.52|ng/dl|1.01^1.79|>||F|||20051220103034|20051220105004|",
            "L|1|");
        var m = Assert.Single(r.Messages);
        Assert.Equal("000004", m.SampleId);
        Assert.Equal(new DateTimeOffset(2005, 12, 20, 10, 16, 4, TimeSpan.FromHours(3)), m.Results[0].RunAt);
        Assert.Equal("0.270-4.20", m.Results[0].ReferenceRangeText);
        Assert.Equal(">", m.Results[1].Comparator);
        Assert.Equal(1.52m, m.Results[1].NumericValue);
        Assert.Equal("2", m.Results[1].Dilution);

        var qc = Parse(
            "H|\\^&||||||||||P||",
            "P|1",
            "O|1|PCU2|96^0019^1^^CONTROL^NORMAL|ALL|R|20051220104418|||||X\\Q||||||||||||||O",
            "R|1|^^^10^^0|1.45|ulU/ml|1.37^1.97|N||F|||20051220110334|20051220112404|",
            "L|1|");
        Assert.Equal(MessageKind.Qc, Assert.Single(qc.Messages).Kind);
    }

    [Fact]
    public void Queries_in_both_variants_and_cancel()
    {
        var cobas = Assert.Single(Parse(
            "H|\\^&|||cobas-e411^1|||||host|TSREQ^REAL|P|1",
            "Q|1|^^000004^40^0^5^^S1^SC||ALL||||||||O",
            "L|1|N").Messages);
        Assert.Equal(MessageKind.Query, cobas.Kind);
        Assert.Equal("000004", cobas.SampleId);
        Assert.Equal(new QueryDetails("40", "0", "5", "S1", "SC", false), cobas.Query);

        var elecsys = Assert.Single(Parse(
            "H|\\^&||||||||||P||",
            "Q|1|^000004^40^0^5^^SAMPLE^NORMAL||ALL||||||||O",
            "L|1|I").Messages);
        Assert.Equal("000004", elecsys.SampleId);
        Assert.Equal("SAMPLE", elecsys.Query!.SampleType);

        var cancel = Assert.Single(Parse(
            "H|\\^&|||cobas-e411^1|||||host|TSREQ^REAL|P|1",
            "Q|1|^^000004^40^0^5^^S1^SC||ALL||||||||A",
            "L|1|N").Messages);
        Assert.True(cancel.Query!.IsCancel);
    }

    [Fact]
    public void Empty_value_and_escapes_are_handled()
    {
        var r = Parse(
            "H|\\^&|||cobas-e411^1|||||host|RSUPL^REAL|P|1",
            "P|1",
            "O|1|AB&F&C|40^0^5^^S1^SC|^^^10^|R||||||N||||1|||||||20051220095504|||F",
            "R|1|^^^10//not|       ^|ulU/ml||N||X||admin|||E1",
            "L|1|N");
        var m = Assert.Single(r.Messages);
        Assert.Equal("AB|C", m.SampleId);
        Assert.Null(m.Results[0].NumericValue);
        Assert.Equal("X", m.Results[0].ResultStatus);
    }

    [Fact]
    public void Garbage_fails_cleanly()
    {
        Assert.False(Parse("X|garbage").Success);
        Assert.False(Parse("H|\\^&", "R|1|^^^10//not|1^|u||N||F").Success);
    }
}
