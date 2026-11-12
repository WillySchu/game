using Game.Backend.Tests;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Trading;

namespace RaidZero.BackendApi.Tests;

/// <summary>Trading through the Hideout's session, against the real backend.</summary>
public class TraderSessionApiTests
{
    private static readonly ItemCatalog Catalog = CatalogJson.LoadFile(TestPaths.CatalogPath);

    private static PlayerSession Session(ApiHost api) => new(api.Client.BaseAddress!.ToString(), Catalog);

    [Fact]
    public async Task Traders_AreListed_WithTheirOffers()
    {
        await using var api = await ApiHost.StartAsync();
        using var session = Session(api);
        await session.DevLoginAsync("Sam");

        var traders = await session.GetTradersAsync();

        Assert.True(traders.Succeeded, traders.ToString());
        var store = Assert.Single(traders.Value);
        Assert.Equal("General Store", store.DisplayName);
        var rifle = store.Offers.Single(o => o.OfferId == "rifle_ak_basic");
        Assert.Equal(45000, rifle.Price);
        Assert.Equal("rifle_ak_basic", rifle.Item.DefId);
        Assert.Equal("mag_ak_30", Assert.Single(rifle.Item.Children).DefId);
        Assert.Equal(1, rifle.Left);
        Assert.NotNull(rifle.RestocksAt);
    }

    [Fact]
    public async Task BuyingAndSelling_UpdateTheProfile()
    {
        await using var api = await ApiHost.StartAsync();
        using var session = Session(api);
        await session.DevLoginAsync("Sam");
        long money = TradePlanner.Money(session.Profile.Stash);

        var bought = await session.BuyAsync("general_store", "pistol_pm");

        Assert.True(bought.Succeeded, bought.ToString());
        Assert.Equal(14000, bought.Credits);
        Assert.Equal(1, session.Profile.Version);
        Assert.Equal(money - 14000, TradePlanner.Money(session.Profile.Stash));
        var pistol = Assert.Single(bought.ItemIds);
        Assert.Contains(session.Profile.Stash.Children, c => c.Id == pistol);

        var sold = await session.SellAsync("general_store", new[] { pistol });

        Assert.True(sold.Succeeded, sold.ToString());
        Assert.Equal((9000 + 900) / 2, sold.Credits);
        Assert.DoesNotContain(session.Profile.Stash.Children, c => c.Id == pistol);
        Assert.Equal(money - 14000 + 4950, TradePlanner.Money(session.Profile.Stash));
    }

    [Fact]
    public async Task ARefusal_GivesTheReason()
    {
        await using var api = await ApiHost.StartAsync();
        using var session = Session(api);
        await session.DevLoginAsync("Sam");
        var money = TradePlanner.MoneyStacks(session.Profile.Stash)[0].Item;

        var result = await session.SellAsync("general_store", new[] { money.Id });

        Assert.False(result.Succeeded);
        Assert.Equal(TradeErrorCodes.NotForSale, result.Error);
        Assert.Equal(money.Id, result.ItemId);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public async Task AStashChangedElsewhere_IsRefreshed_AndTheTradeSentAgain()
    {
        await using var api = await ApiHost.StartAsync();
        using var here = Session(api);
        using var elsewhere = Session(api);
        await here.DevLoginAsync("Sam");
        await elsewhere.DevLoginAsync("Sam");
        var medkit = elsewhere.Profile.Stash.Children.First(c => c.DefId == "medkit_basic");
        Assert.True((await elsewhere.ApplyAsync(new[] { InventoryOp.Move(medkit.Id, InventoryTarget.Grid(elsewhere.Profile.Stash.Id, 9, 29)) })).Succeeded);

        var bought = await here.BuyAsync("general_store", "medkit_basic");

        Assert.True(bought.Succeeded, bought.ToString());
        Assert.Equal(2, here.Profile.Version);
        Assert.Equal((9, 29), (here.Profile.Stash.Children.Single(c => c.Id == medkit.Id).X, here.Profile.Stash.Children.Single(c => c.Id == medkit.Id).Y));
    }
}
