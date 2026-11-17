using Game.Backend.Tests;
using RaidZero.Backend.Catalog;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Loot;
using RaidZero.Shared.Raid;

namespace RaidZero.Raid.Tests;

/// <summary>The AI's kits, against the real catalog, as the raid server uses them.</summary>
public class AIKitsTests
{
    private static readonly ItemCatalog Catalog = CatalogLoader.LoadFile(TestPaths.CatalogPath);

    [Fact]
    public void TheTargetDummysKit_IsValidGear_ThatFiresReloadsAndWears()
    {
        var kit = AIKits.Build(Catalog, AIKits.TargetDummy, null, null, Uuid7.NewId);

        Assert.Equal(WellKnownDefs.Character, kit.DefId);
        Assert.Empty(InventoryValidator.Validate(Catalog, kit));
        Assert.Equal(kit.Walk().Count(), kit.Walk().Select(n => n.Id).Distinct().Count());

        // An AI is a raid inventory participant like a player, under an ID of its own.
        var raid = new RaidInventoryService(Catalog);
        Assert.True(raid.AddPlayer("ai-1", kit));
        Assert.Equal(30, raid.LoadedRounds("ai-1", SlotNames.Primary));
        Assert.Equal("ammo_545_ps", (raid.FireRound("ai-1", SlotNames.Primary, out var fired), fired).fired);
        Assert.True(raid.Reload("ai-1", SlotNames.Primary).Success);   // the spare in its rig
        Assert.Equal(30, raid.LoadedRounds("ai-1", SlotNames.Primary));
        Assert.True(raid.Wear("ai-1", raid.ArmorOf("ai-1")!.Id, 0.2).Success);

        raid.Kill("ai-1");
        Assert.Equal(RootKind.Body, raid.KindOf(kit.Id));               // its gear is a body to loot
    }

    [Fact]
    public void AKit_LoadsTheGun_FillsEveryMagazine_AndPacksTheRest()
    {
        var kit = new AIKitSpec
        {
            Name = "contractor", Weapon = "rifle_ak_basic", Magazine = "mag_ak_30", Ammo = "ammo_545_ps", SpareMagazines = 3,
            Armor = "armor_vest_light", Rig = "rig_chest_basic", Backpack = "backpack_small", Items = { "medkit_basic" },
        };

        var character = AIKits.Build(Catalog, kit, null, null, Uuid7.NewId);

        Assert.Equal(SlotNames.Primary, AIKits.WeaponSlotOf(character));
        var gun = character.ChildInSlot(SlotNames.Primary)!;
        Assert.Equal("rifle_ak_basic", gun.DefId);
        Assert.Equal(1.0, gun.Durability);
        var magazines = character.Walk().Where(n => n.DefId == "mag_ak_30").ToList();
        Assert.Equal(4, magazines.Count);
        Assert.All(magazines, m => Assert.Equal(30, m.ChildInSlot(SlotNames.Rounds)!.Quantity));
        Assert.Equal(1.0, character.ChildInSlot(SlotNames.Armor)!.Durability);
        Assert.Contains(character.Walk(), n => n.DefId == "medkit_basic");
        Assert.Empty(InventoryValidator.Validate(Catalog, character));
    }

    [Fact]
    public void Extras_AreRolledFromTheLootTable_AndWhatDoesntFitIsLeftOut()
    {
        var kit = new AIKitSpec { Name = "looter", Weapon = "pistol_pm", Magazine = "mag_pm_8", Ammo = "ammo_9x18_pst", Rig = "rig_chest_basic" };
        var table = new LootTable { Name = "junk", MinRolls = 40, MaxRolls = 40, Entries = { new LootEntry { DefId = "medkit_basic", Weight = 1 } } };
        var leftOut = new List<ItemNode>();

        var first = AIKits.Build(Catalog, kit, table, new LootRandom(7), Uuid7.NewId, leftOut);
        var again = AIKits.Build(Catalog, kit, table, new LootRandom(7), Uuid7.NewId);

        Assert.Equal(SlotNames.Secondary, AIKits.WeaponSlotOf(first));      // a handgun goes in the secondary slot
        Assert.Equal("pistol_pm", first.ChildInSlot(SlotNames.Secondary)!.DefId);
        var packed = first.Walk().Count(n => n.DefId == "medkit_basic");
        Assert.True(packed > 0);
        Assert.Equal(40, packed + leftOut.Count);                      // the rig took what it could
        Assert.Equal(packed, again.Walk().Count(n => n.DefId == "medkit_basic")); // the same seed, the same gear
        Assert.Empty(InventoryValidator.Validate(Catalog, first));
    }

    [Fact]
    public void AKitThatDoesntGoTogether_OrNamesAnUnknownItem_IsRefused()
    {
        var wrongMagazine = new AIKitSpec { Name = "bad", Weapon = "rifle_ak_basic", Magazine = "mag_pm_8", Ammo = "ammo_9x18_pst" };
        var unknown = new AIKitSpec { Name = "bad", Weapon = "no_such_item" };
        var specs = new[] { new ItemNode { DefId = "no_such_item", Slot = SlotNames.Primary } };

        Assert.Throws<ItemSpecException>(() => AIKits.Build(Catalog, wrongMagazine, null, null, Uuid7.NewId));
        Assert.Throws<ItemSpecException>(() => AIKits.Build(Catalog, unknown, null, null, Uuid7.NewId));
        Assert.Throws<ItemSpecException>(() => AIKits.Build(Catalog, specs, Uuid7.NewId));
    }

    [Fact]
    public void SpawnRolls_AreRepeatable_AndRespectTheirBounds()
    {
        var counts = Enumerable.Range(0, 200).Select(i => AISpawnRolls.Count(LootRandom.ForContainer(42, "zone" + i), 2, 4)).ToList();
        Assert.All(counts, c => Assert.InRange(c, 2, 4));
        Assert.Equal(new[] { 2, 3, 4 }, counts.Distinct().OrderBy(c => c));
        Assert.Equal(AISpawnRolls.Count(LootRandom.ForContainer(42, "a"), 0, 9), AISpawnRolls.Count(LootRandom.ForContainer(42, "a"), 0, 9));
        Assert.Equal(0, AISpawnRolls.Count(new LootRandom(1), -3, -1));
        Assert.Equal(5, AISpawnRolls.Count(new LootRandom(1), 5, 2));

        var random = new LootRandom(3);
        var picks = Enumerable.Range(0, 1000).Select(_ => AISpawnRolls.PickWeighted(random, new[] { 3, 0, 1 })).ToList();
        Assert.DoesNotContain(1, picks);
        Assert.InRange(picks.Count(p => p == 0), 650, 850);
        Assert.Equal(-1, AISpawnRolls.PickWeighted(random, new[] { 0, -2 }));
        Assert.Equal(-1, AISpawnRolls.PickWeighted(random, new int[0]));
    }

    [Fact]
    public void Factions_FightEveryoneButTheirOwn_AndPlayersFightEveryone()
    {
        Assert.True(Factions.AreHostile(Faction.Players, Faction.Players));
        Assert.True(Factions.AreHostile(Faction.Players, Faction.Looters));
        Assert.True(Factions.AreHostile(Faction.Looters, Faction.Players));
        Assert.False(Factions.AreHostile(Faction.Looters, Faction.Looters));
        Assert.True(Factions.AreHostile(Faction.Looters, Faction.Contractors));
        Assert.True(Factions.AreHostile(Faction.Contractors, Faction.Looters));
        Assert.False(Factions.AreHostile(Faction.Contractors, Faction.Contractors));
        Assert.False(Factions.AreHostile((Faction)9, Faction.Players));
    }
}
