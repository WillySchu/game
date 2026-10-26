using Dapper;
using RaidZero.Backend.Data;

namespace Game.Backend.Tests;

public sealed class MigratorTests : IDisposable
{
    private readonly TempDatabase _temp = new();

    private Database Db => _temp.Database;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EmbeddedMigrations_AreFoundAndNumberedFromOne()
    {
        var migrations = Migrator.LoadEmbedded();

        Assert.NotEmpty(migrations);
        Assert.Equal(1, migrations[0].Version);
        Assert.Equal("initial", migrations[0].Name);
    }

    [Fact]
    public async Task FreshDatabase_GetsEveryTable()
    {
        await Db.InitializeAsync();
        var version = await Migrator.MigrateAsync(Db, Migrator.LoadEmbedded());

        Assert.Equal(Migrator.LoadEmbedded()[^1].Version, version);

        var tables = await Db.ReadAsync(async c =>
            (await c.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table'")).ToHashSet());
        foreach (var table in new[] { "players", "item_instances", "raids", "raid_participants", "ledger_entries", "idempotency_keys", "schema_migrations" })
        {
            Assert.Contains(table, tables);
        }
    }

    [Fact]
    public async Task RunningTwice_IsANoOp()
    {
        var migrations = Migrator.LoadEmbedded();
        await Migrator.MigrateAsync(Db, migrations);
        await Migrator.MigrateAsync(Db, migrations);

        var rows = await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(migrations.Count, rows);
    }

    [Fact]
    public async Task NewMigrations_ApplyOnTopOfOldOnes()
    {
        var first = Migrator.Load([("0001_one.sql", "CREATE TABLE a (x INTEGER);")]);
        var both = Migrator.Load([("0001_one.sql", "CREATE TABLE a (x INTEGER);"), ("0002_two.sql", "CREATE TABLE b (y INTEGER);")]);

        Assert.Equal(1, await Migrator.MigrateAsync(Db, first));
        Assert.Equal(2, await Migrator.MigrateAsync(Db, both));

        var hasB = await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'b'"));
        Assert.Equal(1, hasB);
    }

    [Fact]
    public async Task EditedMigration_RefusesToStart()
    {
        await Migrator.MigrateAsync(Db, Migrator.Load([("0001_one.sql", "CREATE TABLE a (x INTEGER);")]));

        var edited = Migrator.Load([("0001_one.sql", "CREATE TABLE a (x INTEGER, z TEXT);")]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Migrator.MigrateAsync(Db, edited));
        Assert.Contains("edited after it was applied", ex.Message);
    }

    [Fact]
    public async Task DatabaseAheadOfCode_RefusesToStart()
    {
        await Migrator.MigrateAsync(Db, Migrator.Load([("0001_one.sql", "CREATE TABLE a (x INTEGER);"), ("0002_two.sql", "CREATE TABLE b (y INTEGER);")]));

        var older = Migrator.Load([("0001_one.sql", "CREATE TABLE a (x INTEGER);")]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Migrator.MigrateAsync(Db, older));
        Assert.Contains("doesn't know", ex.Message);
    }

    [Fact]
    public async Task FailingMigration_LeavesNothingBehind()
    {
        var broken = Migrator.Load([("0001_broken.sql", "CREATE TABLE a (x INTEGER); THIS IS NOT SQL;")]);

        await Assert.ThrowsAnyAsync<Exception>(() => Migrator.MigrateAsync(Db, broken));

        var leftovers = await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'a'"));
        var recorded = await Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(0, leftovers);
        Assert.Equal(0, recorded);
    }

    [Fact]
    public void Checksum_IgnoresWindowsLineEndings()
    {
        Assert.Equal(Migrator.Checksum("CREATE TABLE a (x INTEGER);\nSELECT 1;\n"), Migrator.Checksum("CREATE TABLE a (x INTEGER);\r\nSELECT 1;\r\n"));
    }

    [Theory]
    [InlineData("initial.sql")]
    [InlineData("v1_initial.sql")]
    [InlineData("0000_zero.sql")]
    public void BadFileNames_AreRejected(string fileName)
    {
        Assert.Throws<InvalidOperationException>(() => Migrator.Load([(fileName, "SELECT 1;")]));
    }

    [Fact]
    public void GapsInNumbering_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Migrator.Load([("0001_a.sql", "SELECT 1;"), ("0003_c.sql", "SELECT 1;")]));
    }
}
