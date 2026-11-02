using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Lims.Audit;
using Lims.Host.Security;
using Lims.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lims.UnitTests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows only (DPAPI)"; }
}

internal static class TestCerts
{
    public static X509Certificate2 SelfSigned(string cn = "localhost")
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(cn);
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Round-trip through PFX so the private key is usable by SslStream on every platform.
        // Exportable: on Windows a PFX-loaded key cannot be re-exported otherwise (the HTTPS test writes it back out).
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "pw"), "pw", X509KeyStorageFlags.Exportable);
    }
}

public sealed class SecretResolverTests
{
    [Fact]
    public void File_reference_is_read_and_trailing_newline_dropped()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "s3cret\n");
            Assert.Equal("s3cret", SecretResolver.Resolve($"file:{path}"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Credential_reference_reads_systemd_credentials_directory_and_refuses_traversal()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var old = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
        try
        {
            File.WriteAllText(Path.Combine(dir, "hmis-secret"), "from-systemd");
            Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", dir);
            Assert.Equal("from-systemd", SecretResolver.Resolve("credential:hmis-secret"));
            Assert.Throws<InvalidOperationException>(() => SecretResolver.Resolve("credential:../etc/passwd"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CREDENTIALS_DIRECTORY", old);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Plaintext_secrets_are_detected_but_references_and_placeholders_are_not()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Lims"] = "Host=db;Password=plain",
            ["Hmis:ClientSecret"] = "dpapi-machine:AAAA",
            ["Hmis:WebhookSecret"] = "SET-IN-SECRET-STORE",
            ["Bootstrap:AdminPassword"] = "",
        }).Build();
        Assert.Equal(["ConnectionStrings:Lims"], SecretResolver.PlaintextSecrets(config));
    }

    [Fact]
    public void Plain_values_pass_through_and_only_references_are_resolved()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "x");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["A"] = "plain", ["B"] = $"file:{path}",
            }).Build();
            var resolved = SecretResolver.ResolveAll(config);
            Assert.Equal("x", Assert.Single(resolved).Value);
        }
        finally { File.Delete(path); }
    }

    [WindowsFact]
    public void Dpapi_machine_and_user_round_trip()
    {
        foreach (var machine in new[] { true, false })
        {
            var protectedValue = SecretResolver.Protect("hunter2-ünicode", machine);
            Assert.StartsWith(machine ? "dpapi-machine:" : "dpapi-user:", protectedValue);
            Assert.DoesNotContain("hunter2", protectedValue);
            Assert.Equal("hunter2-ünicode", SecretResolver.Resolve(protectedValue));
        }
    }

    [Fact]
    public void Dpapi_on_linux_fails_with_a_clear_message()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Throws<PlatformNotSupportedException>(() => SecretResolver.Protect("x", machine: true));
        Assert.Throws<PlatformNotSupportedException>(() => SecretResolver.Resolve("dpapi-machine:AAAA"));
    }
}

public sealed class StorageGateTests
{
    [Theory]
    [InlineData(50, StorageLevel.Ok)]
    [InlineData(20, StorageLevel.Ok)]
    [InlineData(19.9, StorageLevel.Warning)]
    [InlineData(9.9, StorageLevel.Critical)]
    [InlineData(4.9, StorageLevel.Refusing)]
    public void Thresholds_follow_the_design(double free, StorageLevel expected) => Assert.Equal(expected, StorageGate.Classify(free));
}

public sealed class SyslogTlsTests
{
    private static AuditExportRow Row(long seq) => new(seq, new DateTime(2026, 11, 2, 8, 30, 0, DateTimeKind.Utc), "approver1", "result.approved",
        "result", "r-1", null, null, null, "00", "ab12");

    [Fact]
    public void Message_is_rfc5424_with_octet_counting_frame()
    {
        var msg = SyslogTlsAuditSink.Format(Row(7), "lims", "srv1");
        Assert.StartsWith("<109>1 2026-11-02T08:30:00.000000Z srv1 lims - result.approved [lims@32473 seq=\"7\" hash=\"ab12\"] {", msg);
        var frame = Encoding.UTF8.GetString(SyslogTlsAuditSink.Frame(msg));
        Assert.Equal($"{Encoding.UTF8.GetByteCount(msg)} {msg}", frame);
    }

    [Fact]
    public async Task Sink_delivers_framed_events_over_tls_to_a_pinned_server()
    {
        using var cert = TestCerts.SelfSigned();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });
            var messages = new List<string>();
            while (messages.Count < 3) messages.Add(await ReadFramedAsync(ssl));
            return (messages, ssl.SslProtocol);
        });

        using var sink = new SyslogTlsAuditSink(new SyslogOptions
        {
            Host = "localhost", Port = port, AppName = "lims", ServerCertificateThumbprint = cert.Thumbprint,
        });
        await sink.SendAsync([Row(1), Row(2)], default);
        await sink.AnchorAsync(2, "ffee", default);

        var (received, protocol) = await server.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();
        Assert.True(protocol is SslProtocols.Tls12 or SslProtocols.Tls13);
        Assert.Contains("seq=\"1\"", received[0]);
        Assert.Contains("seq=\"2\"", received[1]);
        Assert.Contains("audit.anchor", received[2]);
        Assert.Contains("hash=\"ffee\"", received[2]);
    }

    [Fact]
    public async Task Sink_refuses_an_unpinned_self_signed_server()
    {
        using var cert = TestCerts.SelfSigned();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ssl = new SslStream(client.GetStream());
            try { await ssl.AuthenticateAsServerAsync(cert); } catch { /* client aborts */ }
        });
        using var sink = new SyslogTlsAuditSink(new SyslogOptions { Host = "localhost", Port = port, AppName = "lims" });
        await Assert.ThrowsAsync<AuthenticationException>(() => sink.SendAsync([Row(1)], default));
        listener.Stop();
    }

    private static async Task<string> ReadFramedAsync(Stream s)
    {
        var len = 0;
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one) == 0) throw new EndOfStreamException();
            if (one[0] == (byte)' ') break;
            len = len * 10 + (one[0] - '0');
        }
        var buf = new byte[len];
        await s.ReadExactlyAsync(buf);
        return Encoding.UTF8.GetString(buf);
    }
}

public sealed class HttpsSetupTests
{
    [Fact]
    public async Task Kestrel_serves_only_tls12_or_newer_with_the_configured_pfx()
    {
        using var cert = TestCerts.SelfSigned();
        var pfx = Path.Combine(Path.GetTempPath(), $"lims-test-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx, "pfx-pass"));
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Https:Port"] = "0", ["Https:BindAddress"] = "127.0.0.1",
                ["Https:CertificatePath"] = pfx, ["Https:CertificatePassword"] = "pfx-pass",
            });
            HttpsSetup.Configure(builder);
            await using var app = builder.Build();
            app.MapGet("/ping", () => "pong");
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Assert.StartsWith("https://", address);

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == cert.Thumbprint,
            };
            using var http = new HttpClient(handler);
            Assert.Equal("pong", await http.GetStringAsync(address.Replace("0.0.0.0", "127.0.0.1") + "/ping"));

            var info = app.Services.GetRequiredService<CertificateInfo>();
            Assert.Equal(cert.Thumbprint, info.Thumbprint);
            await app.StopAsync();
        }
        finally { File.Delete(pfx); }
    }

    [Fact]
    public void No_certificate_means_https_is_not_configured()
    {
        Assert.False(new HttpsOptions().Enabled);
        Assert.True(new HttpsOptions { CertificateThumbprint = "AB CD" }.Enabled);
    }
}
