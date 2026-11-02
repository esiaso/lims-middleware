using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Lims.Host.Security;

/// <summary>
/// Resolves protected configuration values (design 9.4). A value may be:
/// <list type="bullet">
/// <item><c>dpapi-machine:BASE64</c> or <c>dpapi-user:BASE64</c>: Windows DPAPI, made with <c>Lims.Host.exe protect-secret</c>.
/// <c>dpapi-user</c> must be created while running as the service account, and only that account can read it.</item>
/// <item><c>credential:NAME</c>: a systemd credential (<c>LoadCredentialEncrypted=</c>) read from <c>$CREDENTIALS_DIRECTORY</c> on Linux.</item>
/// <item><c>file:PATH</c>: a file readable only by the service account.</item>
/// </list>
/// Outside Development and Testing, the service refuses to start if a secret key holds a plain value.
/// </summary>
public static class SecretResolver
{
    public const string DpapiMachine = "dpapi-machine:";
    public const string DpapiUser = "dpapi-user:";
    public const string Credential = "credential:";
    public const string FilePrefix = "file:";

    /// <summary>Configuration keys that must never be plain text in production.</summary>
    public static readonly string[] SecretKeys =
    [
        "ConnectionStrings:Lims", "Hmis:ClientSecret", "Hmis:WebhookSecret", "Bootstrap:AdminPassword",
        "Https:CertificatePassword", "AuditExport:Syslog:ClientCertificatePassword",
    ];

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LimsMiddleware.v1");

    public static bool IsReference(string? value) =>
        value is not null && (value.StartsWith(DpapiMachine, StringComparison.Ordinal) || value.StartsWith(DpapiUser, StringComparison.Ordinal)
            || value.StartsWith(Credential, StringComparison.Ordinal) || value.StartsWith(FilePrefix, StringComparison.Ordinal));

    /// <summary>Returns overrides for every configuration value that is a secret reference.</summary>
    public static IDictionary<string, string?> ResolveAll(IConfiguration config)
    {
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in config.AsEnumerable())
            if (IsReference(value)) resolved[key] = Resolve(value!);
        return resolved;
    }

    /// <summary>Keys among <see cref="SecretKeys"/> that hold a non-empty plain value.</summary>
    public static IReadOnlyList<string> PlaintextSecrets(IConfiguration config) =>
        SecretKeys.Where(k => config[k] is { Length: > 0 } v && !IsReference(v) && !IsPlaceholder(v)).ToList();

    private static bool IsPlaceholder(string v) => v.Contains("SET-IN-SECRET-STORE", StringComparison.Ordinal);

    public static string Resolve(string value)
    {
        if (value.StartsWith(DpapiMachine, StringComparison.Ordinal)) return Unprotect(value[DpapiMachine.Length..], machine: true);
        if (value.StartsWith(DpapiUser, StringComparison.Ordinal)) return Unprotect(value[DpapiUser.Length..], machine: false);
        if (value.StartsWith(Credential, StringComparison.Ordinal))
        {
            var dir = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY")
                ?? throw new InvalidOperationException("credential: secret used but CREDENTIALS_DIRECTORY is not set (systemd LoadCredential)");
            var name = value[Credential.Length..];
            if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid credential name");
            return File.ReadAllText(Path.Combine(dir, name)).TrimEnd('\r', '\n');
        }
        if (value.StartsWith(FilePrefix, StringComparison.Ordinal)) return File.ReadAllText(value[FilePrefix.Length..]).TrimEnd('\r', '\n');
        return value;
    }

    public static string Protect(string plaintext, bool machine)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is only available on Windows; use credential: or file: on Linux.");
        return (machine ? DpapiMachine : DpapiUser) + Convert.ToBase64String(ProtectWindows(Encoding.UTF8.GetBytes(plaintext), machine));
    }

    private static string Unprotect(string base64, bool machine)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A DPAPI-protected secret can only be read on Windows.");
        return Encoding.UTF8.GetString(UnprotectWindows(Convert.FromBase64String(base64), machine));
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data, bool machine) =>
        ProtectedData.Protect(data, Entropy, machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data, bool machine) =>
        ProtectedData.Unprotect(data, Entropy, machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);
}
