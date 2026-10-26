using RaidZero.Backend.Catalog;
using RaidZero.Backend.Players;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace Game.Backend.Tests;

public class StarterKitTests
{
    private static readonly ItemCatalog Catalog = CatalogLoader.LoadFile(TestPaths.CatalogPath);

    [Fact]
    public void CommittedKit_FitsTheCommittedCatalog()
    {
        var kit = StarterKit.LoadFile(TestPaths.StarterKitPath, Catalog);
        var (stash, character) = kit.Instantiate();

        Assert.Equal(WellKnownDefs.Stash, stash.DefId);
        Assert.Equal(WellKnownDefs.Character, character.DefId);
        Assert.Empty(InventoryValidator.Validate(Catalog, stash));
        Assert.Empty(InventoryValidator.Validate(Catalog, character));
        Assert.Contains(character.Children, c => c.Slot == SlotNames.Primary);
    }

    [Fact]
    public void EachInstance_GetsFreshIds()
    {
        var kit = StarterKit.LoadFile(TestPaths.StarterKitPath, Catalog);

        var first = Ids(kit.Instantiate());
        var second = Ids(kit.Instantiate());

        Assert.Equal(first.Count, first.Distinct().Count());
        Assert.Empty(first.Intersect(second));
        Assert.Equal(kit.ItemCount, first.Count);
    }

    [Fact]
    public void GridItemsWithoutAPosition_TakeTheNextFreeSpot()
    {
        var kit = StarterKit.Parse(
            """
            { "stash": [
                { "def_id": "loot_gpu", "slot": "grid" },
                { "def_id": "loot_fuse", "slot": "grid" },
                { "def_id": "loot_fuse", "slot": "grid", "x": 9, "y": 0 }
            ] }
            """, Catalog);

        var positions = kit.Instantiate().Stash.Children.Select(c => (c.X, c.Y)).ToArray();

        Assert.Equal(new (int?, int?)[] { (0, 0), (2, 0), (9, 0) }, positions);
    }

    [Fact]
    public void UnknownItems_StopStartup()
    {
        var ex = Assert.Throws<StarterKitException>(() =>
            StarterKit.Parse("""{ "stash": [{ "def_id": "plasma_rifle", "slot": "grid" }] }""", Catalog));

        Assert.Contains("plasma_rifle", ex.Message);
    }

    [Fact]
    public void ItemsThatBreakTheRules_StopStartup()
    {
        var ex = Assert.Throws<StarterKitException>(() =>
            StarterKit.Parse("""{ "character": [{ "def_id": "loot_fuse", "slot": "primary" }] }""", Catalog));

        Assert.Contains(InventoryErrorCodes.SlotMismatch, ex.Message);
    }

    [Fact]
    public void AKitThatDoesntFit_StopsStartup()
    {
        // The 10x30 stash holds 150 two-cell graphics cards; ask for one more.
        var items = string.Join(",", Enumerable.Repeat("""{ "def_id": "loot_gpu", "slot": "grid" }""", 151));

        var ex = Assert.Throws<StarterKitException>(() => StarterKit.Parse($$"""{ "stash": [{{items}}] }""", Catalog));

        Assert.Contains("no room for loot_gpu", ex.Message);
    }

    [Fact]
    public void BrokenJson_StopsStartup()
    {
        Assert.Throws<StarterKitException>(() => StarterKit.Parse("{ \"stash\": [", Catalog));
    }

    private static List<string> Ids((ItemNode Stash, ItemNode Character) trees) =>
        trees.Stash.Walk().Concat(trees.Character.Walk()).Select(n => n.Id).ToList();
}
