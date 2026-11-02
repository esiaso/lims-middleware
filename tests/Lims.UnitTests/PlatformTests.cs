using System.Text;
using Lims.Hmis;
using Lims.Platform;

namespace Lims.UnitTests;

public class PlatformTests
{
    [Fact]
    public void Accession_has_valid_check_digit_and_round_trips()
    {
        var (display, barcode) = AccessionNumber.Format(new DateOnly(2026, 11, 2), 42);
        Assert.StartsWith("26110200042-", display);
        Assert.Equal(12, barcode.Length);
        Assert.Equal(barcode, AccessionNumber.NormalizeBarcode(display));
        Assert.Equal(barcode, AccessionNumber.NormalizeBarcode(barcode));
        // single digit error is caught
        var wrong = barcode[..5] + (char)('0' + (barcode[5] - '0' + 1) % 10) + barcode[6..];
        Assert.Null(AccessionNumber.NormalizeBarcode(wrong));
        Assert.Null(AccessionNumber.NormalizeBarcode("@40"));
        Assert.Null(AccessionNumber.NormalizeBarcode(null));
    }

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1234567890, "005924")]
    public void Totp_matches_rfc6238_vectors(long unixTime, string expected)
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal(expected, Totp.Compute(key, unixTime / 30));
        var secret = Totp.Base32Encode(key);
        Assert.Equal(key, Totp.Base32Decode(secret));
        Assert.True(Totp.Verify(secret, expected, DateTimeOffset.FromUnixTimeSeconds(unixTime)));
        Assert.False(Totp.Verify(secret, "000000", DateTimeOffset.FromUnixTimeSeconds(unixTime)));
    }

    [Fact]
    public void Webhook_signature_checks_body_secret_and_age()
    {
        var body = Encoding.UTF8.GetBytes("{\"event\":\"order.created\",\"order_id\":\"O1\"}");
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds().ToString();
        var sig = WebhookSignature.Sign("s3cret", ts, body);
        Assert.True(WebhookSignature.Verify("s3cret", ts, sig, body, now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify("other", ts, sig, body, now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify("s3cret", ts, sig, Encoding.UTF8.GetBytes("{}"), now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify("s3cret", ts, sig, body, now.AddMinutes(6), TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify("", ts, sig, body, now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Backoff_follows_design_schedule()
    {
        var rng = new Random(1);
        Assert.InRange(Backoff.Delay(1, rng).TotalSeconds, 9, 11);
        Assert.InRange(Backoff.Delay(5, rng).TotalMinutes, 4.5, 5.5);
        Assert.InRange(Backoff.Delay(20, rng).TotalMinutes, 13.5, 16.5);
    }
}
