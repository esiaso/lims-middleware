using System.Security.Cryptography;

namespace Lims.Platform;

/// <summary>RFC 6238 TOTP (SHA-1, 6 digits, 30 s) for MFA on privileged roles (design 9.2).</summary>
public static class Totp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static string Compute(byte[] key, long timestep, int digits = 6)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(timestep & 0xFF); timestep >>= 8; }
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var mod = (int)Math.Pow(10, digits);
        return (binary % mod).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Accepts the current step and one step either side for clock drift.</summary>
    public static bool Verify(string base32Secret, string? code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6) return false;
        var key = Base32Decode(base32Secret);
        var step = now.ToUnixTimeSeconds() / 30;
        for (var d = -1; d <= 1; d++)
        {
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(Compute(key, step + d)),
                    System.Text.Encoding.ASCII.GetBytes(code)))
                return true;
        }
        return false;
    }

    public static string Base32Encode(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string s)
    {
        var clean = s.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Base32Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Invalid base32 character.");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { output.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return output.ToArray();
    }
}
