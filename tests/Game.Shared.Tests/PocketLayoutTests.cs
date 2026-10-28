using RaidZero.Shared.InventoryUi;

namespace Game.Shared.Tests;

public class PocketLayoutTests
{
    private static (string, float, float)[] Places(PocketArrangement arrangement) =>
        arrangement.Placements.Select(p => (p.Name, p.X, p.Y)).ToArray();

    [Fact]
    public void ASingleGrid_SitsAtTheTopLeft()
    {
        var layout = PocketLayout.Arrange([new PocketSpec("grid", 4, 5)], maxRowWidth: 6, gap: 0.25f);

        Assert.Equal([("grid", 0f, 0f)], Places(layout));
        Assert.Equal((4f, 5f), (layout.Width, layout.Height));
    }

    [Fact]
    public void Pockets_FlowLeftToRightInOrder_AndWrap()
    {
        var layout = PocketLayout.Arrange(
        [
            new PocketSpec("a", 1, 2), new PocketSpec("b", 1, 2), new PocketSpec("c", 2, 2), new PocketSpec("d", 2, 1),
        ], maxRowWidth: 4.5f, gap: 0.5f);

        // a at 0, b at 1.5, c at 3 would end at 5 > 4.5: wraps. Row one is 2 tall; d fits beside c, ending at 4.5.
        Assert.Equal([("a", 0f, 0f), ("b", 1.5f, 0f), ("c", 0f, 2.5f), ("d", 2.5f, 2.5f)], Places(layout));
        Assert.Equal((4.5f, 4.5f), (layout.Width, layout.Height));
    }

    [Fact]
    public void APocketWiderThanARow_StillGetsARowOfItsOwn()
    {
        var layout = PocketLayout.Arrange([new PocketSpec("a", 1, 1), new PocketSpec("wide", 8, 1)], maxRowWidth: 4, gap: 0);

        Assert.Equal([("a", 0f, 0f), ("wide", 0f, 1f)], Places(layout));
    }

    [Fact]
    public void PlacedPockets_GoWhereTheySay_AndTheRestFlowBelowThem()
    {
        var layout = PocketLayout.Arrange(
        [
            new PocketSpec("mag_1", 1, 2, x: 0, y: 0),
            new PocketSpec("loose", 2, 1),
            new PocketSpec("pouch", 2, 2, x: 2, y: 0),
        ], maxRowWidth: 6, gap: 0.25f);

        Assert.Equal([("mag_1", 0f, 0f), ("loose", 0f, 2.25f), ("pouch", 2f, 0f)], Places(layout));
        Assert.Equal((4f, 3.25f), (layout.Width, layout.Height));
    }

    [Fact]
    public void NoGrids_TakeNoSpace()
    {
        var layout = PocketLayout.Arrange([], maxRowWidth: 6, gap: 0.25f);

        Assert.Empty(layout.Placements);
        Assert.Equal((0f, 0f), (layout.Width, layout.Height));
    }
}
