using Dapper;
using Microsoft.Data.Sqlite;
using RaidZero.Backend.Data;
using RaidZero.Shared.Ids;

namespace Game.Backend.Tests;

public sealed class DatabaseTests : IDisposable
{
    private const string Now = "2026-10-26T20:00:00Z";

    private readonly TempDatabase _temp = new();

    private Database Db => _temp.Database;

    public void Dispose() => _temp.Dispose();

    private async Task MigrateAsync()
    {
        await Db.InitializeAsync();
        await Migrator.MigrateAsync(Db, Migrator.LoadEmbedded());
    }

    [Fact]
    public async Task Initialize_TurnsOnWalMode()
    {
        await Db.InitializeAsync();

        var mode = await Db.ReadAsync(c => c.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
        Assert.Equal("wal", mode);
    }

    [Fact]
    public async Task EveryConnection_EnforcesForeignKeys()
    {
        var enabled = await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("PRAGMA foreign_keys;"));
        Assert.Equal(1, enabled);
    }

    [Fact]
    public async Task Write_CommitsWhenTheWorkSucceeds()
    {
        await MigrateAsync();
        var player = Uuid7.NewId();

        await Db.WriteAsync((c, tx) => InsertPlayerWithRootsAsync(c, tx, player, "Sam"));

        var count = await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM item_instances WHERE owner_id = @player", new { player }));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Write_RollsBackWhenTheWorkThrows()
    {
        await MigrateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Db.WriteAsync(async (c, tx) =>
        {
            await InsertPlayerWithRootsAsync(c, tx, Uuid7.NewId(), "Sam");
            throw new InvalidOperationException("boom");
        }));

        var count = await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM players"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Write_RollsBackWhenADeferredForeignKeyFailsAtCommit()
    {
        await MigrateAsync();

        await Assert.ThrowsAsync<SqliteException>(() => Db.WriteAsync((c, tx) => c.ExecuteAsync(
            "INSERT INTO players (id, display_name, stash_root_id, character_root_id, created_at) VALUES (@id, 'Ghost', 'missing-a', 'missing-b', @Now)",
            new { id = Uuid7.NewId(), Now }, transaction: tx)));

        var count = await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM players"));
        Assert.Equal(0, count);
    }

    /// <summary>
    /// Invariant 2 from the backend doc in miniature: two claims on one raid-pool item, and only the
    /// first conditional update matches a row.
    /// </summary>
    [Fact]
    public async Task ConditionalClaim_SucceedsOnlyOnce()
    {
        await MigrateAsync();
        var player = Uuid7.NewId();
        var raid = Uuid7.NewId();
        var item = Uuid7.NewId();

        await Db.WriteAsync(async (c, tx) =>
        {
            await InsertPlayerWithRootsAsync(c, tx, player, "Sam");
            await c.ExecuteAsync(
                "INSERT INTO raids (id, map_id, server_id, state, lease_expires_at, created_at) VALUES (@raid, 'greybox_01', 'dev', 'active', @Now, @Now)",
                new { raid, Now }, transaction: tx);
            await c.ExecuteAsync(
                """
                INSERT INTO item_instances (id, def_id, owner_id, state, raid_id, origin, created_at, updated_at)
                VALUES (@item, 'loot_fuse', NULL, 'in_raid', @raid, 'raid_loot', @Now, @Now)
                """,
                new { item, raid, Now }, transaction: tx);
        });

        Task<int> ClaimAsync() => Db.WriteAsync((c, tx) => c.ExecuteAsync(
            """
            UPDATE item_instances SET state = 'stash', owner_id = @player, raid_id = NULL, updated_at = @Now
            WHERE id = @item AND state = 'in_raid' AND raid_id = @raid
            """,
            new { player, item, raid, Now }, transaction: tx));

        var results = await Task.WhenAll(ClaimAsync(), ClaimAsync());

        Assert.Equal(1, results.Sum());
    }

    private static async Task InsertPlayerWithRootsAsync(SqliteConnection c, SqliteTransaction tx, string playerId, string name)
    {
        var stash = Uuid7.NewId();
        var character = Uuid7.NewId();

        // The player row points at roots that don't exist yet; deferred foreign keys allow this.
        await c.ExecuteAsync(
            "INSERT INTO players (id, display_name, stash_root_id, character_root_id, created_at) VALUES (@playerId, @name, @stash, @character, @Now)",
            new { playerId, name, stash, character, Now }, transaction: tx);
        await c.ExecuteAsync(
            """
            INSERT INTO item_instances (id, def_id, owner_id, state, origin, created_at, updated_at)
            VALUES (@stash, 'stash', @playerId, 'stash', 'starter', @Now, @Now),
                   (@character, 'character', @playerId, 'stash', 'starter', @Now, @Now)
            """,
            new { stash, character, playerId, Now }, transaction: tx);
    }
}
