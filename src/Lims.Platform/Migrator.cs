using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Lims.Platform;

/// <summary>
/// Applies embedded SQL migrations in name order, once each, under an advisory lock.
/// The service refuses to start when the database is ahead of the code (design 10.3).
/// </summary>
public sealed class Migrator(NpgsqlDataSource dataSource, ILogger<Migrator> logger)
{
    private const long LockKey = 0x4C494D53_00000001; // "LIMS" + 1

    public static IReadOnlyList<(string Id, string Sql)> LoadMigrations()
    {
        var asm = typeof(Migrator).Assembly;
        return asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(".Migrations.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var s = asm.GetManifestResourceStream(n)!;
                using var r = new StreamReader(s);
                var id = n[(n.IndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..^4];
                return (id, r.ReadToEnd());
            })
            .ToList();
    }

    private static Task<bool> CanCreateAsync(NpgsqlConnection conn) =>
        conn.ExecuteScalarAsync<bool>("SELECT has_database_privilege(current_database(), 'CREATE')");

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(@k)", new { k = LockKey });
        try
        {
            // The service runs as lims_app, which has no CREATE right; IF NOT EXISTS still needs it, so check first.
            if (await conn.ExecuteScalarAsync<string?>("SELECT to_regclass('public.lims_schema_migration')::text") is null)
                await conn.ExecuteAsync("""
                    CREATE TABLE IF NOT EXISTS public.lims_schema_migration (
                        id text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())
                    """);
            var applied = (await conn.QueryAsync<string>("SELECT id FROM public.lims_schema_migration")).ToHashSet();
            var known = LoadMigrations();
            var unknown = applied.Except(known.Select(k => k.Id)).ToList();
            if (unknown.Count > 0)
                throw new InvalidOperationException(
                    $"Database has migrations this build does not know ({string.Join(", ", unknown)}). Refusing to start an older build against a newer schema.");

            var pending = known.Where(k => !applied.Contains(k.Id)).ToList();
            if (pending.Count > 0 && !await CanCreateAsync(conn))
                throw new InvalidOperationException(
                    $"Migrations {string.Join(", ", pending.Select(p => p.Id))} are pending and this account cannot apply them. " +
                    "Run 'Lims.Host.exe migrate --connection <lims_owner connection>' and deploy/sql/grants.sql, then start the service.");

            foreach (var (id, sql) in pending)
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(sql, transaction: tx);
                await conn.ExecuteAsync("INSERT INTO public.lims_schema_migration (id) VALUES (@id)", new { id }, tx);
                await tx.CommitAsync(ct);
                logger.LogInformation("Applied migration {MigrationId}", id);
            }
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@k)", new { k = LockKey });
        }
    }
}
