namespace Lims.Instruments.Astm;

public static class AstmControl
{
    public const byte STX = 0x02, ETX = 0x03, EOT = 0x04, ENQ = 0x05, ACK = 0x06, LF = 0x0A, CR = 0x0D, NAK = 0x15, ETB = 0x17;

    /// <summary>E1381: at most 240 text characters per frame (247 bytes including framing).</summary>
    public const int MaxFrameText = 240;
}
