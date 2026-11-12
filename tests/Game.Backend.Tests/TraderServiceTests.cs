using Dapper;
using RaidZero.Backend.Catalog;
using RaidZero.Backend.Data;
using RaidZero.Backend.Players;
using RaidZero.Backend.Trading;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Trading;

namespace Game.Backend.Tests;

/// <summary>
/// Buying and selling against a real database, with the committed catalog, starter kit and traders.json. The
/// starter kit has 50,000 credits in the stash, a loaded rifle (30,000 + a 2,500 magazine + 30 rounds at 120) in
/// the primary slot, and the general store pays half of value.
/// </summary>
public sealed class TraderServiceTests : IDisposable
{
    private const string Store = "general_store";

    private static readonly ItemCatalog Catalog = CatalogLoader.LoadFile(TestPaths.CatalogPath);
    private static readonly TraderList Traders = TraderList.LoadFile(TestPaths.TradersPath, Catalog);

    private readonly TempDatabase _temp = new();
    private readonly FixedTime _time = new(new DateTimeOffset(2026, 11, 12, 1, 0, 0, TimeSpan.Zero));
    private readonly PlayerStore _players;
    private readonly TraderService _traders;

    public TraderServiceTests()
    {
        _players = new PlayerStore(_temp.Database, Catalog, StarterKit.LoadFile(TestPaths.StarterKitPath, Catalog), _time);
        _traders = new TraderService(_temp.Database, Catalog, Traders, _time);
    }

    public void Dispose() => _temp.Dispose();

    private async Task<(string PlayerId, Profile Profile)> NewPlayerAsync(string name = "Sam")
    {
        await _temp.Database.InitializeAsync();
        await Migrator.MigrateAsync(_temp.Database, Migrator.LoadEmbedded());
        var login = await _players.LoginOrCreateAsync(name);
        return (login.PlayerId, (await _players.GetProfileAsync(login.PlayerId))!);
    }

    private async Task<Profile> ProfileAsync(string playerId) => (await _players.GetProfileAsync(playerId))!;

    private static long Money(Profile p) => TradePlanner.Money(p.Stash);

    private static int Count(Profile p, string defId) => p.Stash.Walk().Concat(p.Character.Walk()).Count(n => n.DefId == defId);

    private Task<long> ScalarAsync(string sql, object? args = null) => _temp.Database.ReadAsync(c => c.ExecuteScalarAsync<long>(sql, args));

    private Task<string> TextAsync(string sql, object? args = null) => _temp.Database.ReadAsync(c => c.ExecuteScalarAsync<string>(sql, args))!;

    [Fact]
    public async Task Buying_TakesTheMoney_AndPutsTheItemInTheStash()
    {
        var (player, before) = await NewPlayerAsync();

        var result = await _traders.BuyAsync(player, 0, Store, "pistol_pm", 1);

        Assert.Equal(TradeStatus.Done, result.Status);
        Assert.Equal(1, result.ProfileVersion);
        Assert.Equal(14000, result.Credits);
        var after = await ProfileAsync(player);
        Assert.Equal(Money(before) - 14000, Money(after));
        var pistol = after.Stash.Children.Single(c => c.Id == Assert.Single(result.ItemIds));
        Assert.Equal("pistol_pm", pistol.DefId);
        Assert.Equal(1.0, pistol.Durability);
        Assert.Equal("mag_pm_8", Assert.Single(pistol.Children).DefId);
        Assert.Equal(Count(before, "pistol_pm") + 1, Count(after, "pistol_pm"));
        Assert.Equal("purchase", await TextAsync("SELECT origin FROM item_instances WHERE id = @id", new { id = pistol.Id }));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM ledger_entries WHERE kind = 'purchase'"));
    }

    [Fact]
    public async Task Stock_RunsOut_ThenComesBackWhenItRestocks()
    {
        var (player, _) = await NewPlayerAsync();

        Assert.Equal(TradeStatus.Done, (await _traders.BuyAsync(player, 0, Store, "pistol_pm", 2)).Status);
        var third = await _traders.BuyAsync(player, 1, Store, "pistol_pm", 1);

        Assert.Equal(TradeStatus.Rejected, third.Status);
        Assert.Equal(TradeErrorCodes.OutOfStock, third.Error);
        Assert.Contains("More in 3 h 0 min", third.Message); // the 240-minute period runs 00:00 to 04:00 UTC; it's 01:00
        var view = (await _traders.OffersAsync(player, Traders.Find(Store)!)).Single(o => o.Offer.OfferId == "pistol_pm");
        Assert.Equal(0, view.Left);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 4, 0, 0, TimeSpan.Zero), view.RestocksAt);

        _time.Now = new DateTimeOffset(2026, 11, 12, 4, 0, 0, TimeSpan.Zero);

        Assert.Equal(TradeStatus.Done, (await _traders.BuyAsync(player, 1, Store, "pistol_pm", 1)).Status);
        view = (await _traders.OffersAsync(player, Traders.Find(Store)!)).Single(o => o.Offer.OfferId == "pistol_pm");
        Assert.Equal(1, view.Left);
    }

    [Fact]
    public async Task Stock_IsCountedForEachPlayer()
    {
        var (sam, _) = await NewPlayerAsync("Sam");
        var (alex, _) = await NewPlayerAsync("Alex");

        Assert.Equal(TradeStatus.Done, (await _traders.BuyAsync(sam, 0, Store, "rifle_ak_basic", 1)).Status);
        Assert.Equal(TradeStatus.Done, (await _traders.BuyAsync(alex, 0, Store, "rifle_ak_basic", 1)).Status);
    }

    [Fact]
    public async Task Buying_WithoutEnoughMoney_ChangesNothing()
    {
        var (player, _) = await NewPlayerAsync();
        Assert.Equal(TradeStatus.Done, (await _traders.BuyAsync(player, 0, Store, "rifle_ak_basic", 1)).Status);
        var before = await ProfileAsync(player);

        var armor = await _traders.BuyAsync(player, 1, Store, "armor_vest_light", 1);

        Assert.Equal(TradeStatus.Rejected, armor.Status);
        Assert.Equal(TradeErrorCodes.NotEnoughMoney, armor.Error);
        Assert.Equal(1, armor.ProfileVersion);
        var after = await ProfileAsync(player);
        Assert.Equal(1, after.ProfileVersion);
        Assert.Equal(Money(before), Money(after));
        Assert.Equal(Count(before, "armor_vest_light"), Count(after, "armor_vest_light"));
        Assert.Equal(1, await ScalarAsync("SELECT bought FROM trader_purchases WHERE offer_id = 'rifle_ak_basic'"));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM trader_purchases WHERE offer_id = 'armor_vest_light'"));
    }

    [Theory]
    [InlineData("pistol_pm", 0, TradeErrorCodes.InvalidCount)]
    [InlineData("pistol_pm", TraderList.MaxCount + 1, TradeErrorCodes.InvalidCount)]
    [InlineData("pistol_pm", 3, TradeErrorCodes.OutOfStock)]
    [InlineData("plasma_rifle", 1, TradeErrorCodes.UnknownOffer)]
    public async Task Buying_SomethingImpossible_IsRefused(string offerId, int count, string error)
    {
        var (player, _) = await NewPlayerAsync();

        var result = await _traders.BuyAsync(player, 0, Store, offerId, count);

        Assert.Equal(TradeStatus.Rejected, result.Status);
        Assert.Equal(error, result.Error);
        Assert.Equal(0, (await ProfileAsync(player)).ProfileVersion);
    }

    [Fact]
    public async Task UnknownTrader_AndStaleVersion_AreTheirOwnAnswers()
    {
        var (player, _) = await NewPlayerAsync();

        Assert.Equal(TradeStatus.UnknownTrader, (await _traders.BuyAsync(player, 0, "black_market", "pistol_pm", 1)).Status);
        Assert.Equal(TradeStatus.UnknownTrader, (await _traders.SellAsync(player, 0, "black_market", ["x"])).Status);
        var stale = await _traders.BuyAsync(player, 7, Store, "pistol_pm", 1);
        Assert.Equal(TradeStatus.VersionConflict, stale.Status);
        Assert.Equal(0, stale.ProfileVersion);
    }

    [Fact]
    public async Task Selling_EquippedRifle_PaysForEverythingInIt()
    {
        var (player, before) = await NewPlayerAsync();
        var rifle = before.Character.Children.Single(c => c.Slot == "primary");
        var inside = rifle.Walk().Select(n => n.Id).ToList();

        var result = await _traders.SellAsync(player, 0, Store, [rifle.Id]);

        Assert.Equal(TradeStatus.Done, result.Status);
        Assert.Equal((30000 + 2500 + 30 * 120) / 2, result.Credits);
        Assert.Equal(inside, result.ItemIds);
        var after = await ProfileAsync(player);
        Assert.Equal(Money(before) + result.Credits, Money(after));
        Assert.DoesNotContain(after.Character.Children, c => c.Slot == "primary");
        Assert.Equal(inside.Count, await ScalarAsync("SELECT COUNT(*) FROM item_instances WHERE state = 'sold'"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM ledger_entries WHERE kind = 'sell'"));
        // The money went onto the existing stack: no new one.
        Assert.Single(TradePlanner.MoneyStacks(after.Stash));
    }

    [Fact]
    public async Task SendingTheSameSaleTwice_SellsOnce()
    {
        var (player, before) = await NewPlayerAsync();
        var medkit = before.Stash.Children.First(c => c.DefId == "medkit_basic");

        var first = await _traders.SellAsync(player, 0, Store, [medkit.Id]);
        var again = await _traders.SellAsync(player, 0, Store, [medkit.Id]);

        Assert.Equal(TradeStatus.Done, first.Status);
        Assert.Equal(TradeStatus.VersionConflict, again.Status);
        Assert.Equal(Money(before) + 1500, Money(await ProfileAsync(player)));
    }

    [Fact]
    public async Task SellingMoney_OrSomethingThatIsntThere_IsRefused()
    {
        var (player, before) = await NewPlayerAsync();
        var money = TradePlanner.MoneyStacks(before.Stash)[0].Item;

        var cash = await _traders.SellAsync(player, 0, Store, [money.Id]);
        var ghost = await _traders.SellAsync(player, 0, Store, ["0190f1a2-0000-7000-8000-000000000000"]);

        Assert.Equal(TradeErrorCodes.NotForSale, cash.Error);
        Assert.Equal(TradeErrorCodes.ItemNotFound, ghost.Error);
        Assert.Equal(0, (await ProfileAsync(player)).ProfileVersion);
    }

    [Fact]
    public async Task SpendingEveryCredit_UsesUpTheStack()
    {
        var (player, before) = await NewPlayerAsync();
        var money = TradePlanner.MoneyStacks(before.Stash)[0].Item;
        Assert.Equal(50000, money.Quantity);

        // 45,000 for a rifle, then a 4,500 medkit: 500 left. Then sell the medkit back for 1,500.
        Assert.Equal(TradeStatus.Done, (await _traders.BuyAsync(player, 0, Store, "rifle_ak_basic", 1)).Status);
        var medkit = await _traders.BuyAsync(player, 1, Store, "medkit_basic", 1);
        Assert.Equal(TradeStatus.Done, medkit.Status);
        Assert.Equal(500, Money(await ProfileAsync(player)));
        var broke = await _traders.BuyAsync(player, 2, Store, "medkit_basic", 1);
        Assert.Equal(TradeErrorCodes.NotEnoughMoney, broke.Error);

        Assert.Equal(TradeStatus.Done, (await _traders.SellAsync(player, 2, Store, [medkit.ItemIds[0]])).Status);
        Assert.Equal(2000, Money(await ProfileAsync(player)));
    }
}
