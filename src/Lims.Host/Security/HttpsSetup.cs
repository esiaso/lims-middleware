using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Lims.Host.Security;

public sealed class HttpsOptions
{
    public int Port { get; set; } = 443;
    /// <summary>Address to bind; default all interfaces.</summary>
    public string? BindAddress { get; set; }
    /// <summary>Thumbprint of the hospital-issued certificate in LocalMachine\My (Windows).</summary>
    public string? CertificateThumbprint { get; set; }
    /// <summary>PFX path (Linux or testing). Password via a protected secret.</summary>
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(CertificateThumbprint) || !string.IsNullOrWhiteSpace(CertificatePath);
}

/// <summary>
/// HTTPS only with the hospital certificate, TLS 1.2 minimum (design 9.4). When a certificate is configured,
/// no plain HTTP endpoint is opened.
/// </summary>
public static class HttpsSetup
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var opt = builder.Configuration.GetSection("Https").Get<HttpsOptions>() ?? new HttpsOptions();
        if (!opt.Enabled) return;
        var cert = Load(opt);
        if (cert.NotAfter < DateTime.Now) throw new InvalidOperationException($"HTTPS certificate {cert.Thumbprint} expired on {cert.NotAfter:u}");
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            var ip = string.IsNullOrWhiteSpace(opt.BindAddress) ? IPAddress.Any : IPAddress.Parse(opt.BindAddress);
            k.Listen(ip, opt.Port, l => l.UseHttps(h =>
            {
                h.ServerCertificate = cert;
                h.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            }));
        });
        builder.WebHost.UseUrls(); // drop any configured plain-HTTP URLs
        builder.Services.AddSingleton(new CertificateInfo(cert.Subject, cert.Thumbprint, cert.NotAfter));
    }

    public static X509Certificate2 Load(HttpsOptions opt)
    {
        if (!string.IsNullOrWhiteSpace(opt.CertificatePath))
            return X509CertificateLoader.LoadPkcs12FromFile(opt.CertificatePath, opt.CertificatePassword);
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, opt.CertificateThumbprint!.Replace(" ", "", StringComparison.Ordinal), validOnly: false);
        if (found.Count == 0) throw new InvalidOperationException($"Certificate {opt.CertificateThumbprint} not found in LocalMachine\\My");
        var cert = found[0];
        if (!cert.HasPrivateKey) throw new InvalidOperationException("The HTTPS certificate has no private key the service account can read");
        return cert;
    }
}

public sealed record CertificateInfo(string Subject, string Thumbprint, DateTime NotAfter);
