using Game.Backend.Tests;
using RaidZero.Backend.Catalog;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;

namespace RaidZero.Raid.Tests;

/// <summary>The AI's kits, against the real catalog, as the raid server uses them.</summary>
public class AIKitsTests
{
    private static readonly ItemCatalog Catalog = CatalogLoader.LoadFile(TestPaths.CatalogPath);

    [Fact]
    public void TheTargetDummysKit_IsValidGear_ThatFiresReloadsAndWears()
    {
        var kit = AIKits.Build(Catalog, AIKits.TargetDummy, Uuid7.NewId);

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
    public void AKitNamingAnUnknownItem_IsRefused()
    {
        var specs = new[] { new ItemNode { DefId = "no_such_item", Slot = SlotNames.Primary } };

        Assert.Throws<ItemSpecException>(() => AIKits.Build(Catalog, specs, Uuid7.NewId));
    }
}
