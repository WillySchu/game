using System.Text.Json;
using Dapper;
using RaidZero.Backend.Catalog;
using RaidZero.Backend.Data;
using RaidZero.Backend.Players;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace Game.Backend.Tests;

/// <summary>
/// The stash ops service against a real database, starting from the committed starter kit:
///   character: rifle (primary) with a loaded magazine; armor; rig holding a loaded magazine and a medkit; backpack.
///   stash: credits (0,0), ammo 60 (1,0), ammo 60 (2,0), pistol (3,0), pistol ammo (5,0), empty magazine (6,0), medkit (7,0).
/// </summary>
public sealed class StashInventoryTests : IDisposable
{
    private static readonly ItemCatalog Catalog = CatalogLoader.LoadFile(TestPaths.CatalogPath);

    private readonly TempDatabase _temp = new();
    private readonly PlayerStore _players;
    private readonly StashInventory _stash;

    public StashInventoryTests()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 10, 26, 20, 0, 0, TimeSpan.Zero));
        _players = new PlayerStore(_temp.Database, Catalog, StarterKit.LoadFile(TestPaths.StarterKitPath, Catalog), time);
        _stash = new StashInventory(_temp.Database, Catalog, time);
    }

    public void Dispose() => _temp.Dispose();

    private async Task<(string PlayerId, Profile Profile)> NewPlayerAsync(string name = "Sam")
    {
        await _temp.Database.InitializeAsync();
        await Migrator.MigrateAsync(_temp.Database, Migrator.LoadEmbedded());
        var login = await _players.LoginOrCreateAsync(name);
        return (login.PlayerId, (await _players.GetProfileAsync(login.PlayerId))!);
    }

    private async Task<Profile> ProfileAsync(string playerId) => (await _players.GetProfileAsync(playerId))!;

    private static ItemNode StashItem(Profile p, string defId, int nth = 0) => p.Stash.Children.Where(c => c.DefId == defId).ElementAt(nth);

    private static ItemNode Find(Profile p, string id) => p.Stash.Walk().Concat(p.Character.Walk()).Single(n => n.Id == id);

    private Task<long> CountAsync(string sql, object? args = null) => _temp.Database.ReadAsync(c => c.ExecuteScalarAsync<long>(sql, args));

    [Fact]
    public async Task Move_IsSavedAndBumpsTheVersion()
    {
        var (player, profile) = await NewPlayerAsync();
        var medkit = StashItem(profile, "medkit_basic");

        var result = await _stash.ApplyAsync(player, 0, [InventoryOp.Move(medkit.Id, InventoryTarget.Grid(profile.Stash.Id, 9, 29))]);

        Assert.Equal(StashOpsStatus.Applied, result.Status);
        Assert.Equal(1, result.ProfileVersion);
        var after = await ProfileAsync(player);
        Assert.Equal(1, after.ProfileVersion);
        Assert.Equal((9, 29), (Find(after, medkit.Id).X, Find(after, medkit.Id).Y));
    }

    [Fact]
    public async Task Move_IntoAnotherContainerChangesItsParent()
    {
        var (player, profile) = await NewPlayerAsync();
        var medkit = StashItem(profile, "medkit_basic");
        var backpack = profile.Character.Children.Single(c => c.Slot == SlotNames.Backpack);

        await _stash.ApplyAsync(player, 0, [InventoryOp.Move(medkit.Id, InventoryTarget.AnyGridSpot(backpack.Id))]);

        var after = await ProfileAsync(player);
        Assert.Contains(after.Character.Children.Single(c => c.Slot == SlotNames.Backpack).Children, c => c.Id == medkit.Id);
        Assert.DoesNotContain(after.Stash.Children, c => c.Id == medkit.Id);
    }

    [Fact]
    public async Task AStaleVersion_IsAConflictAndChangesNothing()
    {
        var (player, profile) = await NewPlayerAsync();
        var medkit = StashItem(profile, "medkit_basic");
        var move = InventoryOp.Move(medkit.Id, InventoryTarget.Grid(profile.Stash.Id, 9, 29));

        Assert.Equal(StashOpsStatus.Applied, (await _stash.ApplyAsync(player, 0, [move])).Status);
        var second = await _stash.ApplyAsync(player, 0, [InventoryOp.Move(medkit.Id, InventoryTarget.Grid(profile.Stash.Id, 0, 29))]);

        Assert.Equal(StashOpsStatus.VersionConflict, second.Status);
        Assert.Equal(1, second.ProfileVersion);
        Assert.Equal(29, Find(await ProfileAsync(player), medkit.Id).Y);
        Assert.Equal(9, Find(await ProfileAsync(player), medkit.Id).X);
    }

    [Fact]
    public async Task ARejectedOp_ChangesNothing()
    {
        var (player, profile) = await NewPlayerAsync();
        var medkit = StashItem(profile, "medkit_basic");
        var credits = StashItem(profile, "currency_credits");

        var result = await _stash.ApplyAsync(player, 0,
        [
            InventoryOp.Move(medkit.Id, InventoryTarget.Grid(profile.Stash.Id, 9, 29)),
            InventoryOp.Move(medkit.Id, InventoryTarget.Grid(profile.Stash.Id, credits.X!.Value, credits.Y!.Value)),
        ]);

        Assert.Equal(StashOpsStatus.Rejected, result.Status);
        Assert.Equal(1, result.FailedOpIndex);
        Assert.Equal(InventoryErrorCodes.Overlap, result.Error!.Code);
        var after = await ProfileAsync(player);
        Assert.Equal(0, after.ProfileVersion);
        Assert.Equal((7, 0), (Find(after, medkit.Id).X, Find(after, medkit.Id).Y));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM ledger_entries WHERE kind = 'inventory'"));
    }

    [Fact]
    public async Task Split_SavesTheNewStackWithItsSourcesOrigin()
    {
        var (player, profile) = await NewPlayerAsync();
        var ammo = StashItem(profile, "ammo_545_ps");

        var result = await _stash.ApplyAsync(player, 0, [InventoryOp.Split(ammo.Id, 25, InventoryTarget.AnyGridSpot(profile.Stash.Id))]);

        var created = Assert.Single(result.Created);
        var after = await ProfileAsync(player);
        Assert.Equal(25, Find(after, created.ItemId).Quantity);
        Assert.Equal(35, Find(after, ammo.Id).Quantity);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM item_instances WHERE id = @id AND origin = 'starter'", new { id = created.ItemId }));
    }

    [Fact]
    public async Task MergingAWholeStack_MarksItMerged()
    {
        var (player, profile) = await NewPlayerAsync();
        var ammo = StashItem(profile, "ammo_545_ps");
        var split = await _stash.ApplyAsync(player, 0, [InventoryOp.Split(ammo.Id, 20, InventoryTarget.AnyGridSpot(profile.Stash.Id))]);
        var piece = split.Created.Single().ItemId;

        var merge = await _stash.ApplyAsync(player, 1, [InventoryOp.Merge(piece, ammo.Id)]);

        Assert.Equal(StashOpsStatus.Applied, merge.Status);
        var after = await ProfileAsync(player);
        Assert.Equal(60, Find(after, ammo.Id).Quantity);
        Assert.DoesNotContain(after.Stash.Walk(), n => n.Id == piece);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM item_instances WHERE id = @piece AND state = 'merged'", new { piece }));
    }

    [Fact]
    public async Task DiscardingAContainer_DiscardsEverythingInIt()
    {
        var (player, profile) = await NewPlayerAsync();
        var rig = profile.Character.Children.Single(c => c.Slot == SlotNames.Rig);
        var contents = rig.Walk().Select(n => n.Id).ToList();

        await _stash.ApplyAsync(player, 0, [InventoryOp.Discard(rig.Id)]);

        Assert.Equal(4, contents.Count); // rig, magazine, its rounds, medkit
        Assert.Equal(contents.Count, await CountAsync(
            "SELECT COUNT(*) FROM item_instances WHERE state = 'discarded' AND id IN (SELECT value FROM json_each(@ids))",
            new { ids = JsonSerializer.Serialize(contents) }));
        Assert.Null((await ProfileAsync(player)).Character.Children.SingleOrDefault(c => c.Slot == SlotNames.Rig));
    }

    /// <summary>
    /// The ordering trap: the batch is valid, but its updates are listed spare-magazine first. Writing that
    /// magazine's final position (the rifle's magazine slot) before lifting the old one out would hit the
    /// one-item-per-slot index. The two-phase write makes the order irrelevant.
    /// </summary>
    [Fact]
    public async Task SwappingMagazines_SavesWhateverOrderTheChangesComeIn()
    {
        var (player, profile) = await NewPlayerAsync();
        var rifle = profile.Character.Children.Single(c => c.Slot == SlotNames.Primary);
        var loaded = rifle.Children.Single();
        var spare = StashItem(profile, "mag_ak_30");

        var result = await _stash.ApplyAsync(player, 0,
        [
            InventoryOp.Move(spare.Id, InventoryTarget.Grid(profile.Stash.Id, 9, 0)),
            InventoryOp.Move(loaded.Id, InventoryTarget.AnyGridSpot(profile.Stash.Id)),
            InventoryOp.Move(spare.Id, InventoryTarget.NamedSlot(rifle.Id, SlotNames.Magazine)),
        ]);

        Assert.Equal(StashOpsStatus.Applied, result.Status);
        var after = await ProfileAsync(player);
        Assert.Equal(spare.Id, Find(after, rifle.Id).Children.Single().Id);
        Assert.Contains(after.Stash.Children, c => c.Id == loaded.Id);
        Assert.Equal(30, Find(after, loaded.Id).Children.Single().Quantity);
    }

    [Fact]
    public async Task AnotherPlayersItems_AreNotFound()
    {
        var (sam, samProfile) = await NewPlayerAsync("Sam");
        var ann = await _players.LoginOrCreateAsync("Ann");
        var annMedkit = StashItem((await _players.GetProfileAsync(ann.PlayerId))!, "medkit_basic");

        var result = await _stash.ApplyAsync(sam, 0, [InventoryOp.Move(annMedkit.Id, InventoryTarget.AnyGridSpot(samProfile.Stash.Id))]);

        Assert.Equal(StashOpsStatus.Rejected, result.Status);
        Assert.Equal(InventoryErrorCodes.ItemNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task RootContainers_CannotBeMoved()
    {
        var (player, profile) = await NewPlayerAsync();

        var result = await _stash.ApplyAsync(player, 0, [InventoryOp.Discard(profile.Stash.Id)]);

        Assert.Equal(InventoryErrorCodes.RootItem, result.Error!.Code);
    }

    [Fact]
    public async Task ABatchThatChangesNothing_KeepsTheVersionAndWritesNoLedger()
    {
        var (player, profile) = await NewPlayerAsync();
        var medkit = StashItem(profile, "medkit_basic");

        var result = await _stash.ApplyAsync(player, 0, [InventoryOp.Move(medkit.Id, InventoryTarget.Grid(profile.Stash.Id, 7, 0))]);

        Assert.Equal(StashOpsStatus.Applied, result.Status);
        Assert.Equal(0, result.ProfileVersion);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM ledger_entries WHERE kind = 'inventory'"));
    }

    [Fact]
    public async Task EachBatch_WritesOneLedgerEntry()
    {
        var (player, profile) = await NewPlayerAsync();
        var medkit = StashItem(profile, "medkit_basic");
        var rig = profile.Character.Children.Single(c => c.Slot == SlotNames.Rig);

        await _stash.ApplyAsync(player, 0,
        [
            InventoryOp.Move(medkit.Id, InventoryTarget.Grid(profile.Stash.Id, 9, 29)),
            InventoryOp.Discard(rig.Id),
        ]);

        var detail = JsonDocument.Parse(await _temp.Database.ReadAsync(c => c.ExecuteScalarAsync<string>(
            "SELECT detail FROM ledger_entries WHERE kind = 'inventory'")) ?? "{}").RootElement;
        Assert.Equal(1, detail.GetProperty("profile_version").GetInt64());
        Assert.Equal(2, detail.GetProperty("ops").GetInt32());
        Assert.Equal(medkit.Id, detail.GetProperty("updated")[0].GetString());
        Assert.Equal(4, detail.GetProperty("removed").GetArrayLength());
        Assert.Equal("discarded", detail.GetProperty("removed")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task AnUnknownPlayer_IsNotFound()
    {
        await NewPlayerAsync();

        var result = await _stash.ApplyAsync(RaidZero.Shared.Ids.Uuid7.NewId(), 0, [InventoryOp.Discard(RaidZero.Shared.Ids.Uuid7.NewId())]);

        Assert.Equal(StashOpsStatus.PlayerNotFound, result.Status);
    }
}
