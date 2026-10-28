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

        Assert.True(GridPlacement.Fits(_catalog, backpack, "grid", gpu, 2, 0, rotated: false));
        Assert.False(GridPlacement.Fits(_catalog, backpack, "grid", gpu, 1, 0, rotated: false)); // overlaps
        Assert.False(GridPlacement.Fits(_catalog, backpack, "grid", gpu, 3, 0, rotated: false)); // off the edge
        Assert.True(GridPlacement.Fits(_catalog, backpack, "grid", gpu, 3, 0, rotated: true));
    }

    [Fact]
    public void Fits_CanIgnoreTheItemBeingMoved()
    {
        var gpuNode = Node("gpu", "grid", 0, 0);
        var backpack = Node("backpack", children: [gpuNode]);
        var gpu = _catalog.Get("gpu");

        Assert.False(GridPlacement.Fits(_catalog, backpack, "grid", gpu, 1, 0, rotated: false));
        Assert.True(GridPlacement.Fits(_catalog, backpack, "grid", gpu, 1, 0, rotated: false, ignoreItemId: gpuNode.Id));
    }

    [Fact]
    public void Fits_IsFalseForContainersWithoutAGrid()
    {
        var rifle = Node("rifle", durability: 1.0);

        Assert.False(GridPlacement.Fits(_catalog, rifle, "grid", _catalog.Get("fuse"), 0, 0, rotated: false));
    }

    [Fact]
    public void TryFindFreeSpot_TakesTheFirstFreeCellInReadingOrder()
    {
        var backpack = Node("backpack", children: [Node("gpu", "grid", 0, 0)]);

        Assert.True(GridPlacement.TryFindFreeSpot(_catalog, backpack, _catalog.Get("gpu"), out string grid, out int x, out int y, out bool rotated));
        Assert.Equal(("grid", 2, 0, false), (grid, x, y, rotated));
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

        Assert.True(GridPlacement.TryFindFreeSpot(_catalog, backpack, _catalog.Get("gpu"), out string grid, out int x, out int y, out bool rotated));
        Assert.Equal(("grid", 3, 0, true), (grid, x, y, rotated));
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

        Assert.False(GridPlacement.TryFindFreeSpot(_catalog, backpack, _catalog.Get("fuse"), out _, out _, out _, out _));
    }

    // ---- several grids (pockets) ----

    [Fact]
    public void Pockets_AreSeparateGrids_ThatItemsCantStraddle()
    {
        var vest = Node("vest", children: [Node("fuse", "left", 0, 0)]);
        var gpu = _catalog.Get("gpu");
        var fuse = _catalog.Get("fuse");

        // The vest has 2x2 cells in all, but each pocket is 1x2: a 2x1 gpu only fits standing up.
        Assert.False(GridPlacement.Fits(_catalog, vest, "left", gpu, 0, 0, rotated: false));
        Assert.False(GridPlacement.Fits(_catalog, vest, "right", gpu, 0, 0, rotated: false));
        Assert.True(GridPlacement.Fits(_catalog, vest, "right", gpu, 0, 0, rotated: true));

        // An item only takes up room in its own pocket.
        Assert.False(GridPlacement.Fits(_catalog, vest, "left", fuse, 0, 0, rotated: false));
        Assert.True(GridPlacement.Fits(_catalog, vest, "right", fuse, 0, 0, rotated: false));
    }

    [Fact]
    public void Fits_IsFalseForAGridTheContainerDoesntHave()
    {
        var vest = Node("vest");

        Assert.False(GridPlacement.Fits(_catalog, vest, "grid", _catalog.Get("fuse"), 0, 0, rotated: false));
        Assert.False(GridPlacement.Fits(_catalog, vest, "middle", _catalog.Get("fuse"), 0, 0, rotated: false));
    }

    [Fact]
    public void TryFindFreeSpot_FillsPocketsInOrder()
    {
        var vest = Node("vest", children: [Node("fuse", "left", 0, 0), Node("fuse", "left", 0, 1)]);

        Assert.True(GridPlacement.TryFindFreeSpot(_catalog, vest, _catalog.Get("fuse"), out string grid, out int x, out int y, out _));
        Assert.Equal(("right", 0, 0), (grid, x, y));

        Assert.True(GridPlacement.TryFindFreeSpot(_catalog, vest, "right", _catalog.Get("gpu"), out x, out y, out bool rotated));
        Assert.Equal((0, 0, true), (x, y, rotated));
        Assert.False(GridPlacement.TryFindFreeSpot(_catalog, vest, "left", _catalog.Get("fuse"), out _, out _, out _));
    }
}
