using RaidZero.Shared.Catalog;
using RaidZero.Shared.InventoryUi;
using static Game.Shared.Tests.TestCatalog;

namespace Game.Shared.Tests;

public class ItemTooltipTests
{
    private readonly ItemCatalog _catalog = Build();

    private ItemTooltipContent Tooltip(RaidZero.Shared.Inventory.ItemNode node, string? description = null) =>
        ItemTooltip.Build(_catalog, node, description)!;

    private static string? Line(ItemTooltipContent content, string label) =>
        content.Lines.FirstOrDefault(l => l.Label == label)?.Value;

    [Fact]
    public void Title_IsTheDisplayName_OrTheDefIdWithoutOne()
    {
        var catalog = new ItemCatalog([new ItemDefinition { DefId = "named", DisplayName = "A named thing" }, new ItemDefinition { DefId = "bare" }]);

        Assert.Equal("A named thing", ItemTooltip.Build(catalog, Node("named"))!.Title);
        Assert.Equal("bare", ItemTooltip.Build(catalog, Node("bare"))!.Title);
    }

    [Theory]
    [InlineData("rifle", "Long gun")]
    [InlineData("mag_30", "Magazine")]
    [InlineData("ammo", "Ammunition")]
    [InlineData("backpack", "Backpack")]
    [InlineData("gpu", "Item")]
    public void Category_ComesFromTheTags(string defId, string expected) => Assert.Equal(expected, Tooltip(Node(defId)).Category);

    [Fact]
    public void Condition_ShowsForItemsThatWear_AsAPercentage()
    {
        Assert.Equal("82%", Line(Tooltip(Node("rifle", durability: 0.824)), "Condition"));
        Assert.Equal("100%", Line(Tooltip(Node("rifle", durability: 1.0)), "Condition"));
    }

    [Fact]
    public void Condition_IsLeftOut_ForItemsThatDontWear_OrHaveNoValue()
    {
        Assert.Null(Line(Tooltip(Node("gpu", durability: 0.5)), "Condition"));
        Assert.Null(Line(Tooltip(Node("rifle")), "Condition"));
    }

    [Fact]
    public void Stack_ShowsForStackables_OutOfTheMaximum()
    {
        Assert.Equal("37 / 60", Line(Tooltip(Node("ammo", quantity: 37)), "Stack"));
        Assert.Null(Line(Tooltip(Node("gpu")), "Stack"));
    }

    [Fact]
    public void Magazine_ShowsItsRounds_AndTheirCartridge()
    {
        var content = Tooltip(Node("mag_30", children: [Node("ammo", "rounds", quantity: 24)]));

        Assert.Equal("24 / 30 (ammo)", Line(content, "Rounds"));
        Assert.Equal("0 / 30", Line(Tooltip(Node("mag_30")), "Rounds"));
    }

    [Fact]
    public void Weapon_ShowsItsMagazine_AndTheMagazinesRounds()
    {
        var loaded = Tooltip(Node("rifle", children: [Node("mag_30", "magazine", children: [Node("ammo", "rounds", quantity: 7)])]));

        Assert.Equal("mag_30", Line(loaded, "Magazine"));
        Assert.Equal("7 / 30 (ammo)", Line(loaded, "Rounds"));
    }

    [Fact]
    public void Weapon_WithoutAMagazine_SaysSo_AndHasNoRoundsLine()
    {
        var empty = Tooltip(Node("rifle"));

        Assert.Equal("None", Line(empty, "Magazine"));
        Assert.Null(Line(empty, "Rounds"));
    }

    [Fact]
    public void Container_ShowsItemsAndCellsUsed_CountingOnlyWhatsInItsGrids()
    {
        var backpack = Node("backpack", children:
        [
            Node("gpu", "grid", 0, 0),
            Node("gpu", "grid", 0, 1, rotated: true),
            Node("ammo", "grid", 3, 0, quantity: 60),
        ]);

        Assert.Equal("3 items, 5 / 12 cells", Line(Tooltip(backpack), "Contents"));
        Assert.Equal("Empty, 0 / 12 cells", Line(Tooltip(Node("backpack")), "Contents"));
        Assert.Equal("1 item, 1 / 12 cells", Line(Tooltip(Node("backpack", children: [Node("fuse", "grid", 0, 0)])), "Contents"));
    }

    [Fact]
    public void Container_WithSeveralPockets_AddsThemUp()
    {
        var vest = Node("vest", children: [Node("fuse", "left", 0, 0), Node("fuse", "right", 0, 1)]);

        Assert.Equal("2 items, 2 / 4 cells", Line(Tooltip(vest), "Contents"));
    }

    [Fact]
    public void APlainItem_HasNoLines()
    {
        Assert.Empty(Tooltip(Node("gpu")).Lines);
    }

    [Fact]
    public void Description_IsKept_ButBlankOnesAreDropped()
    {
        Assert.Equal("A graphics card.", Tooltip(Node("gpu"), "  A graphics card. ").Description);
        Assert.Null(Tooltip(Node("gpu"), "   ").Description);
        Assert.Null(Tooltip(Node("gpu")).Description);
    }

    [Fact]
    public void AnUnknownItem_StillGetsATooltip()
    {
        var content = Tooltip(Node("no_such_item"));

        Assert.Equal("no_such_item", content.Title);
        Assert.Empty(content.Lines);
    }

    [Fact]
    public void LinesComeInAFixedOrder()
    {
        var loaded = Tooltip(Node("rifle", durability: 0.5, children: [Node("mag_30", "magazine", children: [Node("ammo", "rounds", quantity: 1)])]));

        Assert.Equal(new[] { "Condition", "Magazine", "Rounds" }, loaded.Lines.Select(l => l.Label));
    }
}
