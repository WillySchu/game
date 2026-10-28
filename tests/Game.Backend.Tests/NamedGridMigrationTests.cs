using Dapper;
using Microsoft.Data.Sqlite;
using RaidZero.Backend.Data;
using RaidZero.Shared.Ids;

namespace Game.Backend.Tests;

/// <summary>Migration 0005: items in a grid store the grid's name as their slot, so containers can have pockets.</summary>
public sealed class NamedGridMigrationTests : IDisposable
{
    private const string Now = "2026-10-28T20:00:00Z";

    private readonly TempDatabase _temp = new();
    private string _player = "";
    private string _stash = "";
    private string _character = "";

    private Database Db => _temp.Database;

    public void Dispose() => _temp.Dispose();

    private async Task SetUpAsync(int? throughVersion = null)
    {
        await Db.InitializeAsync();
        var migrations = Migrator.LoadEmbedded();
        await Migrator.MigrateAsync(Db, throughVersion is null ? migrations : migrations.Where(m => m.Version <= throughVersion).ToList());

        (_player, _stash, _character) = (Uuid7.NewId(), Uuid7.NewId(), Uuid7.NewId());
        await Db.WriteAsync((c, tx) => c.ExecuteAsync(
            """
            INSERT INTO players (id, display_name, stash_root_id, character_root_id, created_at)
            VALUES (@player, 'Sam', @stash, @character, @Now);
            INSERT INTO item_instances (id, def_id, owner_id, state, origin, created_at, updated_at)
            VALUES (@stash, 'stash', @player, 'stash', 'starter', @Now, @Now),
                   (@character, 'character', @player, 'stash', 'starter', @Now, @Now);
            """,
            new { player = _player, stash = _stash, character = _character, Now }, transaction: tx));
    }

    private Task InsertAsync(string parentId, string? slot, int? x, int? y, string defId = "loot_fuse") => Db.WriteAsync((c, tx) => c.ExecuteAsync(
        """
        INSERT INTO item_instances (id, def_id, owner_id, state, parent_id, slot, grid_x, grid_y, origin, created_at, updated_at)
        VALUES (@id, @defId, @player, 'stash', @parentId, @slot, @x, @y, 'starter', @Now, @Now)
        """,
        new { id = Uuid7.NewId(), defId, player = _player, parentId, slot, x, y, Now }, transaction: tx));

    [Fact]
    public void TheRebuild_RunsWithForeignKeysOff()
    {
        Assert.True(Migrator.LoadEmbedded().Single(m => m.Version == 5).DisableForeignKeys);
    }

    [Fact]
    public async Task AGridPosition_CanBeInAnyNamedGrid_ButNeedsOne()
    {
        await SetUpAsync();

        await InsertAsync(_stash, "grid", 0, 0);
        await InsertAsync(_stash, "left_pocket", 0, 0);
        await InsertAsync(_stash, "left_pocket", 0, 1); // grids hold many items; only named slots are unique

        await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(_stash, null, 1, 1));
    }

    [Fact]
    public async Task ANamedSlot_StillHoldsOneLiveItem()
    {
        await SetUpAsync();
        await InsertAsync(_character, "primary", null, null, "rifle_ak_basic");

        await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(_character, "primary", null, null, "rifle_ak_basic"));
    }

    [Fact]
    public async Task UpgradingFrom0004_KeepsGridItemsAsTheyWere()
    {
        await SetUpAsync(throughVersion: 4);
        await InsertAsync(_stash, "grid", 3, 2);
        await InsertAsync(_character, "primary", null, null, "rifle_ak_basic");

        await Migrator.MigrateAsync(Db, Migrator.LoadEmbedded());

        var rows = await Db.ReadAsync(c => c.QueryAsync<string>(
            "SELECT slot || ' ' || IFNULL(grid_x, '-') || ',' || IFNULL(grid_y, '-') FROM item_instances WHERE parent_id IS NOT NULL ORDER BY slot"));
        Assert.Equal(["grid 3,2", "primary -,-"], rows);
        Assert.Equal("ok", await Db.ReadAsync(c => c.ExecuteScalarAsync<string>("PRAGMA integrity_check")));
    }
}
