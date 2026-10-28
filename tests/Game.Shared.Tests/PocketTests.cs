using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.InventoryUi;
using static Game.Shared.Tests.TestCatalog;

namespace Game.Shared.Tests;

/// <summary>
/// Containers with several grids: the test catalog's vest has two 1x2 pockets, "left" and "right". Each pocket
/// is its own grid, and an item can't straddle two.
/// </summary>
public class PocketTests
{
    private readonly ItemCatalog _catalog = Build();

    private static string[] Codes(IReadOnlyList<InventoryError> errors) => errors.Select(e => e.Code).ToArray();

    private IReadOnlyList<InventoryError> Validate(ItemNode root) => InventoryValidator.Validate(_catalog, root);

    // ---- validation ----

    [Fact]
    public void ItemsInDifferentPockets_DontCollide_EvenAtTheSameCell()
    {
        var vest = Node("vest", children: [Node("fuse", "left", 0, 0), Node("fuse", "right", 0, 0), Node("ammo", "left", 0, 1, quantity: 10)]);

        Assert.Empty(Validate(vest));
    }

    [Fact]
    public void ItemsInTheSamePocket_Collide()
    {
        var vest = Node("vest", children: [Node("fuse", "left", 0, 0), Node("fuse", "left", 0, 0)]);

        Assert.Equal([InventoryErrorCodes.Overlap], Codes(Validate(vest)));
    }

    [Fact]
    public void EachPocketHasItsOwnBounds()
    {
        // 2x1 doesn't fit a 1-wide pocket, though the vest as a whole is 2 wide.
        var vest = Node("vest", children: [Node("gpu", "left", 0, 0)]);

        Assert.Equal([InventoryErrorCodes.OutOfBounds], Codes(Validate(vest)));
    }

    [Fact]
    public void APocketTheContainerDoesntHave_IsRefused()
    {
        var mainGrid = Node("vest", children: [Node("fuse", "grid", 0, 0)]);   // the vest has no grid named "grid"
        var unknown = Node("vest", children: [Node("fuse", "middle", 0, 0)]);

        Assert.Equal([InventoryErrorCodes.SlotMismatch], Codes(Validate(mainGrid)));
        Assert.Contains(InventoryErrorCodes.SlotMismatch, Codes(Validate(unknown)));
    }

    [Fact]
    public void ItemsInAPocket_NeedAPosition()
    {
        var vest = Node("vest", children: [Node("fuse", "left")]);

        Assert.Equal([InventoryErrorCodes.PositionMismatch], Codes(Validate(vest)));
    }

    // ---- moves ----

    private (InventoryTree Tree, ItemNode Vest, ItemNode Fuse, ItemNode Gpu) Stash()
    {
        var vest = Node("vest", "grid", 0, 0, children: [Node("fuse", "left", 0, 0)]);
        var fuse = Node("fuse", "grid", 3, 0);
        var gpu = Node("gpu", "grid", 4, 0);
        var stash = Node("stash", children: [vest, fuse, gpu]);
        return (new InventoryTree(_catalog, [stash]), vest, fuse, gpu);
    }

    private static ItemNode Get(InventoryTree tree, string id)
    {
        Assert.True(tree.TryGet(id, out var node));
        return node;
    }

    [Fact]
    public void AMoveCanNameAPocket()
    {
        var (tree, vest, fuse, _) = Stash();

        var result = tree.Apply([InventoryOp.Move(fuse.Id, InventoryTarget.InGrid(vest.Id, "right", 0, 1))]);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(("right", 0, 1), (Get(tree, fuse.Id).Slot, Get(tree, fuse.Id).X!.Value, Get(tree, fuse.Id).Y!.Value));
    }

    [Fact]
    public void AMoveIntoAPocketChecksThatPocketsBoundsAndContents()
    {
        var (tree, vest, fuse, gpu) = Stash();

        var tooWide = tree.Check([InventoryOp.Move(gpu.Id, InventoryTarget.InGrid(vest.Id, "right", 0, 0))]);
        var standingUp = tree.Check([InventoryOp.Move(gpu.Id, InventoryTarget.InGrid(vest.Id, "right", 0, 0, rotated: true))]);
        var taken = tree.Check([InventoryOp.Move(fuse.Id, InventoryTarget.InGrid(vest.Id, "left", 0, 0))]);
        var mainGrid = tree.Check([InventoryOp.Move(fuse.Id, InventoryTarget.Grid(vest.Id, 0, 0))]);

        Assert.Equal(InventoryErrorCodes.OutOfBounds, tooWide.Error.Code);
        Assert.True(standingUp.Success);
        Assert.Equal(InventoryErrorCodes.Overlap, taken.Error.Code);
        Assert.Equal(InventoryErrorCodes.SlotMismatch, mainGrid.Error.Code);
    }

    [Fact]
    public void AnySpot_FillsThePocketsInOrder()
    {
        var (tree, vest, fuse, gpu) = Stash();

        Assert.True(tree.Apply([InventoryOp.Move(fuse.Id, InventoryTarget.AnyGridSpot(vest.Id))]).Success);
        Assert.Equal(("left", 0, 1), (Get(tree, fuse.Id).Slot, Get(tree, fuse.Id).X!.Value, Get(tree, fuse.Id).Y!.Value));

        // Left is full now; the gpu goes to the right pocket, turned to fit.
        Assert.True(tree.Apply([InventoryOp.Move(gpu.Id, InventoryTarget.AnyGridSpot(vest.Id))]).Success);
        Assert.Equal(("right", true), (Get(tree, gpu.Id).Slot, Get(tree, gpu.Id).Rotated));

        // Both pockets are full now.
        var full = tree.Check([InventoryOp.Move(Get(tree, vest.Id).Children[0].Id, InventoryTarget.AnyGridSpot(vest.Id))]);
        Assert.Equal(InventoryErrorCodes.NoSpace, full.Error.Code);
    }

    [Fact]
    public void AnySpotInOnePocket_OnlyLooksThere()
    {
        var (tree, vest, fuse, _) = Stash();

        var right = tree.Apply([InventoryOp.Move(fuse.Id, InventoryTarget.AnySpotIn(vest.Id, "right"))]);

        Assert.True(right.Success);
        Assert.Equal(("right", 0, 0), (Get(tree, fuse.Id).Slot, Get(tree, fuse.Id).X!.Value, Get(tree, fuse.Id).Y!.Value));
    }

    [Fact]
    public void StacksInAPocket_HaveNoSlotCap()
    {
        var pocketAmmo = Node("ammo", "left", 0, 1, quantity: 50);
        var vest = Node("vest", "grid", 0, 0, children: [pocketAmmo]);
        var loose = Node("ammo", "grid", 3, 0, quantity: 30);
        var tree = new InventoryTree(_catalog, [Node("stash", children: [vest, loose])]);

        Assert.True(tree.Apply([InventoryOp.Merge(loose.Id, pocketAmmo.Id)]).Success);
        Assert.Equal(60, Get(tree, pocketAmmo.Id).Quantity); // the ammo's own max, not some slot limit
    }

    // ---- the catalog ----

    [Fact]
    public void GridNames_MustBeValidUniqueAndNotSlots()
    {
        var defs = new List<ItemDefinition>
        {
            new() { DefId = "dupes", Grids = [new GridSpec { Name = "a", Width = 1, Height = 1 }, new GridSpec { Name = "a", Width = 1, Height = 1 }] },
            new() { DefId = "bad_name", Grids = [new GridSpec { Name = "Left Pocket", Width = 1, Height = 1 }] },
            new() { DefId = "empty", Grids = [new GridSpec { Name = "a", Width = 0, Height = 1 }] },
            new()
            {
                DefId = "clash",
                Grids = [new GridSpec { Name = "magazine", Width = 1, Height = 1 }],
                Slots = [new SlotSpec { Name = "magazine", Accepts = ["mag"] }],
            },
        };

        var errors = ItemCatalog.Validate(defs);

        Assert.Contains(errors, e => e.StartsWith("dupes:") && e.Contains("duplicate grid 'a'"));
        Assert.Contains(errors, e => e.StartsWith("bad_name:") && e.Contains("grid names"));
        Assert.Contains(errors, e => e.StartsWith("empty:") && e.Contains("width and height"));
        Assert.Contains(errors, e => e.StartsWith("clash:") && e.Contains("both a grid and a slot"));
    }

    [Fact]
    public void TheHash_CoversGridNamesSizesAndOrder()
    {
        string Hash(params GridSpec[] grids) => CatalogHash.Compute([new ItemDefinition { DefId = "vest", Grids = grids.ToList() }]);
        GridSpec G(string name, int w, int h) => new() { Name = name, Width = w, Height = h };

        var baseline = Hash(G("left", 1, 2), G("right", 1, 2));

        Assert.Equal(baseline, Hash(G("left", 1, 2), G("right", 1, 2)));
        Assert.NotEqual(baseline, Hash(G("right", 1, 2), G("left", 1, 2)));  // order decides which fills first
        Assert.NotEqual(baseline, Hash(G("left", 1, 2), G("other", 1, 2)));
        Assert.NotEqual(baseline, Hash(G("left", 1, 2), G("right", 1, 3)));
        Assert.NotEqual(baseline, Hash(G("left", 1, 2)));
    }

    // ---- the inventory screen ----

    [Fact]
    public void DroppingOnAPocketCell_TargetsThatPocket()
    {
        var (tree, vest, fuse, _) = Stash();

        var plan = DropResolver.Resolve(tree, fuse.Id, false, DropTarget.GridCell(vest.Id, "right", 0, 1));

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal((vest.Id, "right", 0, 1), (plan.GridId, plan.GridName, plan.X, plan.Y));
        Assert.Equal("right", Assert.Single(plan.Ops).To.Slot);
    }

    [Fact]
    public void DroppingOnAPocketedContainer_PutsItInTheFirstPocketWithRoom()
    {
        var (tree, vest, _, gpu) = Stash();

        var plan = DropResolver.Resolve(tree, gpu.Id, false, DropTarget.OnItem(vest.Id));

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal(DropAction.PutInside, plan.Action);
        Assert.True(tree.Apply(plan.Ops).Success);
        // The left pocket has one free cell under its fuse; the gpu needs two even turned, so it goes right.
        Assert.Equal(("right", true), (Get(tree, gpu.Id).Slot, Get(tree, gpu.Id).Rotated));
    }
}
