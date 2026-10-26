using RaidZero.Shared.Inventory;
using static Game.Shared.Tests.TestCatalog;

namespace Game.Shared.Tests;

public class GridPlacementTests
{
    private readonly RaidZero.Shared.Catalog.ItemCatalog _catalog = Build();

    [Fact]
    public void Fits_RespectsBoundsAndOtherItems()
    {
        var backpack = Node("backpack", children: [Node("gpu", "grid", 0, 0)]);
        var gpu = _catalog.Get("gpu");

        Assert.True(GridPlacement.Fits(_catalog, backpack, gpu, 2, 0, rotated: false));
        Assert.False(GridPlacement.Fits(_catalog, backpack, gpu, 1, 0, rotated: false)); // overlaps
        Assert.False(GridPlacement.Fits(_catalog, backpack, gpu, 3, 0, rotated: false)); // off the edge
        Assert.True(GridPlacement.Fits(_catalog, backpack, gpu, 3, 0, rotated: true));
    }

    [Fact]
    public void Fits_CanIgnoreTheItemBeingMoved()
    {
        var gpuNode = Node("gpu", "grid", 0, 0);
        var backpack = Node("backpack", children: [gpuNode]);
        var gpu = _catalog.Get("gpu");

        Assert.False(GridPlacement.Fits(_catalog, backpack, gpu, 1, 0, rotated: false));
        Assert.True(GridPlacement.Fits(_catalog, backpack, gpu, 1, 0, rotated: false, ignoreItemId: gpuNode.Id));
    }

    [Fact]
    public void Fits_IsFalseForContainersWithoutAGrid()
    {
        var rifle = Node("rifle", durability: 1.0);

        Assert.False(GridPlacement.Fits(_catalog, rifle, _catalog.Get("fuse"), 0, 0, rotated: false));
    }

    [Fact]
    public void TryFindFreeSpot_TakesTheFirstFreeCellInReadingOrder()
    {
        var backpack = Node("backpack", children: [Node("gpu", "grid", 0, 0)]);

        Assert.True(GridPlacement.TryFindFreeSpot(_catalog, backpack, _catalog.Get("gpu"), out int x, out int y, out bool rotated));
        Assert.Equal((2, 0, false), (x, y, rotated));
    }

    [Fact]
    public void TryFindFreeSpot_FallsBackToRotating()
    {
        // Fill a 4x3 backpack except its rightmost column, a 1x3 strip.
        // A 2x1 gpu can't fit unrotated, but fits there rotated to 1x2.
        var backpack = Node("backpack", children:
        [
            Node("gpu", "grid", 0, 0), Node("fuse", "grid", 2, 0),
            Node("gpu", "grid", 0, 1), Node("fuse", "grid", 2, 1),
            Node("gpu", "grid", 0, 2), Node("fuse", "grid", 2, 2),
        ]);

        Assert.True(GridPlacement.TryFindFreeSpot(_catalog, backpack, _catalog.Get("gpu"), out int x, out int y, out bool rotated));
        Assert.Equal((3, 0, true), (x, y, rotated));
    }

    [Fact]
    public void TryFindFreeSpot_FailsWhenFull()
    {
        var backpack = Node("backpack", children:
        [
            Node("gpu", "grid", 0, 0), Node("gpu", "grid", 2, 0),
            Node("gpu", "grid", 0, 1), Node("gpu", "grid", 2, 1),
            Node("gpu", "grid", 0, 2), Node("gpu", "grid", 2, 2),
        ]);

        Assert.False(GridPlacement.TryFindFreeSpot(_catalog, backpack, _catalog.Get("fuse"), out _, out _, out _));
    }
}
