using System.Data;
using Npgsql;

namespace Lims.Platform;

/// <summary>Thin wrapper over a pooled NpgsqlDataSource with a transaction helper.</summary>
public sealed class Db(NpgsqlDataSource dataSource)
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) => await DataSource.OpenConnectionAsync(ct);

    public async Task<T> InTransactionAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct,
        IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        await using var conn = await DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(isolation, ct);
        var result = await work(conn, tx);
        await tx.CommitAsync(ct);
        return result;
    }

    public Task InTransactionAsync(Func<NpgsqlConnection, NpgsqlTransaction, Task> work, CancellationToken ct) =>
        InTransactionAsync<bool>(async (c, t) => { await work(c, t); return true; }, ct);
}
