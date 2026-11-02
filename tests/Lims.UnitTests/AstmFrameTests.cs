using System.Text;
using Lims.Instruments.Astm;
using Lims.Sim.E411;

namespace Lims.UnitTests;

public class AstmFrameTests
{
    [Fact]
    public void Checksum_is_sum_modulo_256_as_two_hex_digits()
    {
        // "1" + "L|1|N\r" + ETX
        var bytes = Encoding.ASCII.GetBytes("1L|1|N\r").Concat(new byte[] { AstmControl.ETX }).ToArray();
        var expected = (bytes.Sum(b => b) % 256).ToString("X2");
        Assert.Equal(expected, AstmFrame.Checksum(bytes));
    }

    [Fact]
    public void Middleware_frames_match_the_independent_simulator_byte_for_byte()
    {
        var ours = new AstmFrame(3, Encoding.ASCII.GetBytes("R|1|^^^10//not|1.25^|uIU/mL||N||F||admin|||E1\r"), true).ToBytes();
        var sim = E411Simulator.Frame(3, "R|1|^^^10//not|1.25^|uIU/mL||N||F||admin|||E1\r", true);
        Assert.Equal(sim, ours);
    }

    [Fact]
    public void Roundtrip_parse()
    {
        var f = new AstmFrame(7, Encoding.ASCII.GetBytes("H|\\^&|||host^1\r"), false);
        var parsed = AstmFrame.TryParse(f.ToBytes(), out var err);
        Assert.Null(err);
        Assert.NotNull(parsed);
        Assert.Equal(7, parsed!.FrameNumber);
        Assert.False(parsed.IsFinal);
        Assert.Equal(f.Text, parsed.Text);
    }

    [Fact]
    public void Bad_checksum_is_rejected()
    {
        var bytes = E411Simulator.Frame(1, "L|1|N\r", true, corrupt: true);
        Assert.Null(AstmFrame.TryParse(bytes, out var err));
        Assert.Contains("checksum", err);
    }

    [Fact]
    public void Control_character_inside_text_is_rejected()
    {
        var bytes = E411Simulator.Frame(1, "L|1\u0005|N\r", true);
        Assert.Null(AstmFrame.TryParse(bytes, out var err));
        Assert.Contains("control", err);
    }

    [Fact]
    public void Long_records_split_into_240_character_ETB_frames_and_numbers_wrap()
    {
        var longRecord = "C|1|I|" + new string('x', 600);
        var msg = "H|\\^&\r" + longRecord + "\rL|1|N\r";
        var frames = AstmFrame.BuildFrames(msg);
        Assert.Equal(1 + 3 + 1, frames.Count); // 607 chars + CR = 3 frames
        Assert.All(frames, f => Assert.True(f.Text.Length <= AstmControl.MaxFrameText));
        Assert.False(frames[1].IsFinal);
        Assert.False(frames[2].IsFinal);
        Assert.True(frames[3].IsFinal);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, frames.Select(f => f.FrameNumber));
        Assert.All(frames, f => Assert.True(f.ToBytes().Length <= 247));

        var many = string.Concat(Enumerable.Range(0, 9).Select(i => $"R|{i}|x\r"));
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 0, 1 }, AstmFrame.BuildFrames(many).Select(f => f.FrameNumber));
    }
}
