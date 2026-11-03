using Lims.Audit;
using Lims.Host.Security;
using Lims.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Lims.Host;

/// <summary>
/// Administrative commands used by the installer and runbooks:
///   protect-secret [--user]           read a secret from stdin, print a dpapi-machine: (or dpapi-user:) value
///   migrate --connection CS           apply schema migrations (run as lims_owner)
///   verify-audit --connection CS      recompute the audit chain; exit 0 valid, 2 broken (restore test, design 10.5)
///   verify-export FILE.zip --key REF  check a signed audit export from the Audit screen; exit 0 valid, 2 not valid
/// </summary>
public static class LimsCli
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "protect-secret" or "migrate" or "verify-audit" or "verify-export";

    public static async Task<int> RunAsync(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            switch (args[0])
            {
                case "protect-secret":
                {
                    // Windows PowerShell can prefix piped input with a UTF-8 byte order mark; it is never part of a secret.
                    var secret = (await stdin.ReadLineAsync())?.TrimStart('\uFEFF').TrimEnd('\r', '\n');
                    if (string.IsNullOrEmpty(secret)) { await stderr.WriteLineAsync("no secret on stdin"); return 1; }
                    await stdout.WriteLineAsync(SecretResolver.Protect(secret, machine: !args.Contains("--user")));
                    return 0;
                }
                case "migrate":
                {
                    await using var ds = DataSource(args);
                    await new Migrator(ds, NullLogger<Migrator>.Instance).MigrateAsync(default);
                    await stdout.WriteLineAsync("migrations applied");
                    return 0;
                }
                case "verify-audit":
                {
                    await using var ds = DataSource(args);
                    var result = await new AuditVerifier(new Db(ds)).VerifyAsync(default);
                    await stdout.WriteLineAsync(Json.Serialize(result));
                    return result.Valid ? 0 : 2;
                }
                case "verify-export":
                {
                    var k = Array.IndexOf(args, "--key");
                    if (args.Length < 2 || k < 0 || k + 1 >= args.Length) { await stderr.WriteLineAsync("usage: verify-export FILE.zip --key SECRET-REF"); return 1; }
                    await using var file = File.OpenRead(args[1]);
                    var r = AuditPackage.Verify(file, SecretResolver.Resolve(args[k + 1]));
                    await stdout.WriteLineAsync(r.Detail);
                    return r.Valid ? 0 : 2;
                }
            }
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync(ex.Message);
            return 1;
        }
        return 1;
    }

    private static NpgsqlDataSource DataSource(string[] args)
    {
        var i = Array.IndexOf(args, "--connection");
        if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("--connection is required");
        return new NpgsqlDataSourceBuilder(SecretResolver.Resolve(args[i + 1])).Build();
    }
}
