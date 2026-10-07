using Litos.SoftwareFactory.Core.Ports;
using Npgsql;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>
/// One host per factory database: a PostgreSQL session advisory lock held on a dedicated
/// connection for the host's lifetime. A second host cannot take it and refuses to start.
/// Closing the connection, or losing it, releases the lock, so the host checks it is still held
/// and stops itself when it is not (docs/software-factory/m2-architecture.md §3.5).
/// </summary>
/// <remarks>The connection is never pooled: a pooled connection returned to the pool would keep
/// the lock for whoever used it next. A transaction-mode pooler between the host and the
/// database cannot hold a session lock at all; connect directly or through a session-mode one.</remarks>
public sealed class PostgresHostInstanceLock(string connectionString) : IHostInstanceLock
{
    /// <summary>"LITOSHOS". Session and transaction advisory locks share one key space, so this
    /// must differ from the claim transaction's key ("LITOSFAC").</summary>
    internal const long Key = 0x4C49544F53484F53;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NpgsqlConnection? _connection;

    public async Task<string?> AcquireAsync(CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 30 };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);

        await using (var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection))
        {
            command.Parameters.AddWithValue("key", Key);
            if (await command.ExecuteScalarAsync(ct) is true)
            {
                _connection = connection;
                return null;
            }
        }

        await connection.DisposeAsync();
        return "Another factory host is already running against this database. Stop it first: "
            + "each host marks the tasks it finds running as Interrupted when it starts, which would stop the other host's tasks.";
    }

    public async Task<bool> IsHeldAsync(CancellationToken ct)
    {
        if (_connection is null)
            return false;

        await _gate.WaitAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("SELECT 1", _connection);
            await command.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException)
        {
            // The session is gone, and the lock with it.
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
        _connection = null;
        _gate.Dispose();
    }
}
