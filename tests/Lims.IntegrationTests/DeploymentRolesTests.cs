using Dapper;
using Lims.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Lims.IntegrationTests;

/// <summary>
/// The installed service runs as lims_app, which cannot create objects (design 9.3). Migrations are applied by
/// lims_owner during install; the service's own startup check must then work with lims_app's grants alone.
/// </summary>
public sealed class DeploymentRolesTests
{
    private const string AppRole = "lims_app";
    private const string AppPassword = "integration-test-app-pw";

    [Fact]
    public async Task Service_account_starts_on_a_migrated_database_and_refuses_pending_migrations()
    {
        var db = "lims_roles_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = new NpgsqlConnection(LimsFixture.AdminConnectionString))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {db}");
            // Roles are cluster wide: create them once, as roles.sql would.
            await admin.ExecuteAsync($"""
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppRole}') THEN CREATE ROLE {AppRole} LOGIN; END IF;
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'lims_readonly') THEN CREATE ROLE lims_readonly; END IF;
                END $$;
                ALTER ROLE {AppRole} LOGIN PASSWORD '{AppPassword}';
                """);
        }
        var adminCs = new NpgsqlConnectionStringBuilder(LimsFixture.AdminConnectionString) { Database = db }.ConnectionString;
        var appCs = new NpgsqlConnectionStringBuilder(adminCs) { Username = AppRole, Password = AppPassword }.ConnectionString;
        try
        {
            await using (var owner = NpgsqlDataSource.Create(adminCs))
            {
                await new Migrator(owner, NullLogger<Migrator>.Instance).MigrateAsync(default);
                await using var c = await owner.OpenConnectionAsync();
                await c.ExecuteAsync("REVOKE ALL ON SCHEMA public FROM PUBLIC");
                await c.ExecuteAsync(await File.ReadAllTextAsync(RepoFile("deploy/sql/grants.sql")));
            }

            await using var app = NpgsqlDataSource.Create(appCs);
            await new Migrator(app, NullLogger<Migrator>.Instance).MigrateAsync(default); // nothing pending: no DDL needed

            await using (var owner = NpgsqlDataSource.Create(adminCs))
            await using (var c = await owner.OpenConnectionAsync())
                await c.ExecuteAsync("DELETE FROM public.lims_schema_migration WHERE id = (SELECT max(id) FROM public.lims_schema_migration)");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new Migrator(app, NullLogger<Migrator>.Instance).MigrateAsync(default));
            Assert.Contains("pending", ex.Message);
            Assert.Contains("lims_owner", ex.Message);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(LimsFixture.AdminConnectionString);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
        }
    }

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }
}
