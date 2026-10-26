using Dapper;
using Microsoft.Data.Sqlite;

namespace RaidZero.Backend.Data;

/// <summary>
/// Opens SQLite connections with the right settings and runs work inside transactions.
///
/// Rules for everything that touches the database:
/// - All item and profile changes go through <see cref="WriteAsync{T}"/>, never a bare connection.
/// - Inside a write, pass <c>transaction: tx</c> to every Dapper call. Microsoft.Data.Sqlite throws
///   if a command on a connection with an open transaction doesn't carry it.
/// - Write explicit SQL. Conditional updates with a row-count check are how claims stay single-use.
/// </summary>
public sealed class Database
{
    // foreign_keys is per connection and off by default in SQLite. busy_timeout covers other processes
    // (a migration tool, a DB browser) holding the write lock. synchronous=NORMAL is safe with WAL.
    private const string PerConnectionPragmas =
        "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;";

    private readonly string _connectionString;

    // SQLite allows one writer at a time. Queuing writers here, rather than letting them contend on the
    // file lock, keeps latency predictable. This goes away with the move to Postgres.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public Database(string databasePath)
    {
        FilePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string FilePath { get; }

    /// <summary>Switches the file to WAL mode, which persists in the file. Call once at startup.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var mode = await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode = WAL;");
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Could not enable WAL mode on {FilePath} (got '{mode}').");
        }
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await connection.ExecuteAsync(PerConnectionPragmas);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs read-only work on its own connection. Each statement sees a consistent snapshot.</summary>
    public async Task<T> ReadAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        return await work(connection);
    }

    /// <summary>
    /// Runs work inside a write transaction that commits only if the work returns normally.
    /// It starts with BEGIN IMMEDIATE, taking the write lock up front: a deferred transaction that
    /// reads first and writes later can deadlock against another writer and fail with SQLITE_BUSY.
    /// </summary>
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> work, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var result = await work(connection, transaction);
            await transaction.CommitAsync(ct);
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task WriteAsync(Func<SqliteConnection, SqliteTransaction, Task> work, CancellationToken ct = default) =>
        WriteAsync(async (connection, transaction) =>
        {
            await work(connection, transaction);
            return true;
        }, ct);
}
