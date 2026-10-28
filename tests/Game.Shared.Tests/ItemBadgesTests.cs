using RaidZero.Shared.Catalog;
using RaidZero.Shared.InventoryUi;
using static Game.Shared.Tests.TestCatalog;

namespace Game.Shared.Tests;

public class ItemBadgesTests
{
    private readonly ItemCatalog _catalog = Build();

    [Theory]
    [InlineData("rifle", ItemBadges.Weapon)]
    [InlineData("mag_30", ItemBadges.Magazine)]
    [InlineData("ammo", ItemBadges.Ammo)]
    [InlineData("backpack", ItemBadges.Gear)]
    [InlineData("gpu", ItemBadges.Other)]
    public void Category_ComesFromTheTags(string defId, string expected) =>
        Assert.Equal(expected, ItemBadges.Category(_catalog.Get(defId)));

    [Fact]
    public void Category_OfValuablesAndBarter()
    {
        Assert.Equal(ItemBadges.Valuable, ItemBadges.Category(new ItemDefinition { DefId = "x", Tags = ["currency"] }));
        Assert.Equal(ItemBadges.Barter, ItemBadges.Category(new ItemDefinition { DefId = "y", Tags = ["barter"] }));
    }

    [Fact]
    public void Count_OfAStack_IsItsSize() => Assert.Equal("45", ItemBadges.Count(_catalog, Node("ammo", quantity: 45)));

    [Fact]
    public void Count_OfASingleStackableItem_IsStillShown() => Assert.Equal("1", ItemBadges.Count(_catalog, Node("ammo")));

    [Fact]
    public void Count_OfAPlainItem_IsBlank() => Assert.Equal("", ItemBadges.Count(_catalog, Node("gpu")));

    [Fact]
    public void Count_OfAMagazine_IsRoundsOutOfCapacity()
    {
        Assert.Equal("20/30", ItemBadges.Count(_catalog, Node("mag_30", children: [Node("ammo", "rounds", quantity: 20)])));
        Assert.Equal("0/30", ItemBadges.Count(_catalog, Node("mag_30")));
    }

    [Fact]
    public void Count_OfAWeapon_IsItsMagazinesRounds()
    {
        var loaded = Node("rifle", children: [Node("mag_30", "magazine", children: [Node("ammo", "rounds", quantity: 7)])]);

        Assert.Equal("7/30", ItemBadges.Count(_catalog, loaded));
        Assert.Equal("-", ItemBadges.Count(_catalog, Node("rifle")));
    }

    [Fact]
    public void Count_OfAnUnknownItem_IsBlank() => Assert.Equal("", ItemBadges.Count(_catalog, Node("no_such_item")));
}
