using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using RaidZero.Backend.Data;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace Game.Backend.Tests;

/// <summary>Migrations 0002 and 0003 (the 'discarded' and 'merged' states) and the foreign-keys-off rebuild path they rely on.</summary>
public sealed class ItemStateMigrationTests : IDisposable
{
    private const string Now = "2026-10-26T20:00:00Z";

    private readonly TempDatabase _temp = new();

    private Database Db => _temp.Database;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Directive_CountsOnlyOnTheFirstLine()
    {
        var migrations = Migrator.Load(
        [
            ("0001_a.sql", "-- migrator: foreign_keys=off\nSELECT 1;"),
            ("0002_b.sql", "SELECT 1;"),
            ("0003_c.sql", "-- some notes\n-- migrator: foreign_keys=off\nSELECT 1;"),
            ("0004_d.sql", "  --   Migrator:  FOREIGN_KEYS = OFF  \r\nSELECT 1;"),
        ]);

        Assert.Equal(new[] { true, false, false, true }, migrations.Select(m => m.DisableForeignKeys).ToArray());
        Assert.True(Migrator.LoadEmbedded().Single(m => m.Version == 2).DisableForeignKeys);
        Assert.True(Migrator.LoadEmbedded().Single(m => m.Version == 3).DisableForeignKeys);
    }

    [Fact]
    public async Task ItemStates_MatchTheDatabaseConstraint()
    {
        await MigrateAsync();

        var sql = await Db.ReadAsync(c => c.ExecuteScalarAsync<string>("SELECT sql FROM sqlite_master WHERE name = 'item_instances'"));
        var list = Regex.Match(sql!, @"state\s+TEXT NOT NULL CHECK \(state IN \(([^)]*)\)\)").Groups[1].Value;
        var inDatabase = Regex.Matches(list, "'([a-z_]+)'").Select(m => m.Groups[1].Value).Order().ToArray();

        Assert.Equal(ItemStates.All.Order().ToArray(), inDatabase);
    }

    [Theory]
    [InlineData(ItemStates.Discarded)]
    [InlineData(ItemStates.Merged)]
    public async Task FreshDatabase_AcceptsRemovedItemStates(string state)
    {
        await MigrateAsync();
        var player = await InsertPlayerAsync();

        await Db.WriteAsync((c, tx) => c.ExecuteAsync(
            """
            INSERT INTO item_instances (id, def_id, owner_id, state, origin, created_at, updated_at)
            VALUES (@id, 'loot_fuse', @player, @state, 'starter', @Now, @Now)
            """,
            new { id = Uuid7.NewId(), player, state, Now }, transaction: tx));

        var count = await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM item_instances WHERE state = @state", new { state }));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UpgradingAnExistingDatabase_KeepsEveryRowAndRule()
    {
        // A database that has only run 0001, with a player and an equipped rifle in it.
        await Db.InitializeAsync();
        await Migrator.MigrateAsync(Db, Migrator.LoadEmbedded().Take(1).ToList());
        var player = await InsertPlayerAsync();
        var character = await Db.ReadAsync(c => c.ExecuteScalarAsync<string>("SELECT character_root_id FROM players WHERE id = @player", new { player }));
        await InsertRifleAsync(character!, player);

        var version = await Migrator.MigrateAsync(Db, Migrator.LoadEmbedded());

        Assert.Equal(Migrator.LoadEmbedded()[^1].Version, version);
        Assert.Equal(3, await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM item_instances")));
        Assert.Equal("ok", await Db.ReadAsync(c => c.ExecuteScalarAsync<string>("PRAGMA integrity_check")));

        // Foreign keys are enforced again, and still point at the rebuilt table.
        Assert.Equal(1, await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("PRAGMA foreign_keys")));
        await Assert.ThrowsAsync<SqliteException>(() => Db.WriteAsync((c, tx) => c.ExecuteAsync(
            "INSERT INTO players (id, display_name, stash_root_id, character_root_id, created_at) VALUES (@id, 'Ghost', 'nope', 'nope', @Now)",
            new { id = Uuid7.NewId(), Now }, transaction: tx)));

        // The recreated unique index still allows one live item per named slot.
        await Assert.ThrowsAsync<SqliteException>(() => InsertRifleAsync(character!, player));
    }

    [Theory]
    [InlineData(RemovalReason.Discarded, ItemStates.Discarded)]
    [InlineData(RemovalReason.Merged, ItemStates.Merged)]
    public void EveryRemovalReason_HasAState(RemovalReason reason, string state)
    {
        Assert.Equal(state, ItemStates.ForRemoval(reason));
        Assert.Contains(state, ItemStates.All);
    }

    [Fact]
    public void ForRemoval_CoversEveryReason()
    {
        foreach (var reason in Enum.GetValues<RemovalReason>())
        {
            Assert.Contains(ItemStates.ForRemoval(reason), ItemStates.All);
        }
    }

    [Fact]
    public async Task ARebuildThatWouldOrphanRows_IsRolledBack()
    {
        var setup = ("0001_setup.sql", """
            CREATE TABLE parent (id TEXT PRIMARY KEY);
            CREATE TABLE child (id TEXT PRIMARY KEY, parent_id TEXT NOT NULL REFERENCES parent (id));
            INSERT INTO parent VALUES ('p');
            INSERT INTO child VALUES ('c', 'p');
            """);
        var careless = ("0002_careless_rebuild.sql", """
            -- migrator: foreign_keys=off
            CREATE TABLE parent_new (id TEXT PRIMARY KEY);
            DROP TABLE parent;
            ALTER TABLE parent_new RENAME TO parent;
            """);

        await Migrator.MigrateAsync(Db, Migrator.Load([setup]));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Migrator.MigrateAsync(Db, Migrator.Load([setup, careless])));

        Assert.Contains("1 row(s)", ex.Message);
        Assert.Equal(1, await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations")));
        Assert.Equal(1, await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM parent")));
        Assert.Equal(0, await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'parent_new'")));
    }

    private async Task MigrateAsync()
    {
        await Db.InitializeAsync();
        await Migrator.MigrateAsync(Db, Migrator.LoadEmbedded());
    }

    private async Task<string> InsertPlayerAsync()
    {
        var player = Uuid7.NewId();
        await Db.WriteAsync((c, tx) => c.ExecuteAsync(
            """
            INSERT INTO players (id, display_name, stash_root_id, character_root_id, created_at)
            VALUES (@player, 'Sam', @stash, @character, @Now);
            INSERT INTO item_instances (id, def_id, owner_id, state, origin, created_at, updated_at)
            VALUES (@stash, 'stash', @player, 'stash', 'starter', @Now, @Now),
                   (@character, 'character', @player, 'stash', 'starter', @Now, @Now);
            """,
            new { player, stash = Uuid7.NewId(), character = Uuid7.NewId(), Now }, transaction: tx));
        return player;
    }

    private Task InsertRifleAsync(string characterId, string player) => Db.WriteAsync((c, tx) => c.ExecuteAsync(
        """
        INSERT INTO item_instances (id, def_id, owner_id, state, parent_id, slot, durability, origin, created_at, updated_at)
        VALUES (@id, 'rifle_ak_basic', @player, 'stash', @characterId, 'primary', 0.9, 'starter', @Now, @Now)
        """,
        new { id = Uuid7.NewId(), player, characterId, Now }, transaction: tx));
}
