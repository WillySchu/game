using Game.Backend.Tests;
using RaidZero.Backend.Players;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Trading;

namespace RaidZero.BackendApi.Tests;

/// <summary>The Hideout trader screen's sell list and words, over a starter-kit profile.</summary>
public class TraderSellListTests
{
    private const double Half = 0.5;

    private static readonly ItemCatalog Catalog = CatalogJson.LoadFile(TestPaths.CatalogPath);
    private static readonly StarterKit Kit = StarterKit.LoadFile(TestPaths.StarterKitPath, Catalog);

    private static PlayerProfile Profile()
    {
        var (stash, character) = Kit.Instantiate();
        return new PlayerProfile("p1", "Sam", 0, stash, character, null, null);
    }

    private static ItemNode First(ItemNode root, string defId) => root.Walk().First(n => n.DefId == defId);

    [Fact]
    public void Adding_ListsTheItem_AndTotalsTheTradersPrice()
    {
        var profile = Profile();
        var list = new TraderSellList(Catalog);
        int changes = 0;
        list.Changed += () => changes++;
        var rifle = First(profile.Character, "rifle_ak_basic");
        var medkit = profile.Stash.Children.First(c => c.DefId == "medkit_basic");

        Assert.Null(list.Add(profile, Half, rifle.Id));
        Assert.Null(list.Add(profile, Half, medkit.Id));

        Assert.Equal(new[] { rifle.Id, medkit.Id }, list.ItemIds);
        Assert.Equal(TraderPrices.SellPrice(Catalog, rifle, Half), list.PriceOf(profile, Half, rifle.Id));
        Assert.Equal(TraderPrices.SellPrice(Catalog, rifle, Half) + 1500, list.Total(profile, Half));
        Assert.Equal(2, changes);
        // Nothing moved: the list is only a list.
        Assert.Contains(profile.Character.Children, c => c.Id == rifle.Id);
    }

    [Fact]
    public void Money_AndThingsWithMoneyInThem_AreRefusedWithAReason()
    {
        var profile = Profile();
        var list = new TraderSellList(Catalog);
        var money = TradePlanner.MoneyStacks(profile.Stash)[0].Item;
        var backpack = First(profile.Character, "backpack_small");
        profile.Stash.Children.Remove(money);
        money.Slot = "grid";
        (money.X, money.Y) = (0, 0);
        backpack.Children.Add(money);

        Assert.Equal("Money isn't for sale.", list.WhyNot(profile, Half, money.Id));
        Assert.Equal("Money isn't for sale.", list.Add(profile, Half, money.Id));
        Assert.Null(list.WhyNot(profile, Half, First(profile.Character, "rifle_ak_basic").Id));
        Assert.Contains("money in the", list.Add(profile, Half, backpack.Id));
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void ListingAContainer_TakesWhatsInsideOffTheList()
    {
        var profile = Profile();
        var list = new TraderSellList(Catalog);
        var rifle = First(profile.Character, "rifle_ak_basic");
        var magazine = rifle.Children.Single();

        Assert.Null(list.Add(profile, Half, magazine.Id));
        Assert.Null(list.Add(profile, Half, rifle.Id));

        Assert.Equal(new[] { rifle.Id }, list.ItemIds);
        Assert.Contains("which is being sold too", list.Add(profile, Half, magazine.Id));
    }

    [Fact]
    public void Prune_DropsWhatsGone_KeepingTheRest()
    {
        var profile = Profile();
        var list = new TraderSellList(Catalog);
        var rifle = First(profile.Character, "rifle_ak_basic");
        var medkit = profile.Stash.Children.First(c => c.DefId == "medkit_basic");
        list.Add(profile, Half, rifle.Id);
        list.Add(profile, Half, medkit.Id);

        profile.Character.Children.Remove(rifle); // sold, or taken into a raid
        list.Prune(profile, Half);

        Assert.Equal(new[] { medkit.Id }, list.ItemIds);
        list.Prune(null, Half);
        Assert.Equal(0, list.Count);
    }

    [Fact]
    public void Text_ReadsNaturally()
    {
        var now = new DateTimeOffset(2026, 11, 12, 1, 0, 0, TimeSpan.Zero);
        var pistol = new ItemNode { DefId = "pistol_pm", Children = { new ItemNode { DefId = "mag_pm_8", Slot = "magazine" } } };
        var ammo = new ItemNode { DefId = "ammo_545_ps", Quantity = 60 };

        Assert.Equal("18,050 cr", TraderText.Credits(18050));
        Assert.Equal("0 cr", TraderText.Credits(0));
        Assert.Equal("3 h 5 min", TraderText.Wait(TimeSpan.FromMinutes(184.5)));
        Assert.Equal("40 min", TraderText.Wait(TimeSpan.FromMinutes(40)));
        Assert.Equal("a moment", TraderText.Wait(TimeSpan.FromSeconds(20)));
        Assert.Equal("2 left", TraderText.Stock(new TraderOfferInfo("x", 1, pistol, 2, 2, now.AddHours(3)), now));
        Assert.Equal("Sold out · more in 3 h 0 min", TraderText.Stock(new TraderOfferInfo("x", 1, pistol, 2, 0, now.AddHours(3)), now));
        Assert.Null(TraderText.Stock(new TraderOfferInfo("x", 1, pistol, null, null, null), now));
        Assert.EndsWith(" ×60", TraderText.Title(Catalog, ammo));
        Assert.Null(TraderText.Contents(Catalog, ammo));
        Assert.Equal("with " + Catalog.Get("mag_pm_8").DisplayName, TraderText.Contents(Catalog, pistol));
    }
}
