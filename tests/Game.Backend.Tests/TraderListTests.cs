using RaidZero.Backend.Catalog;
using RaidZero.Backend.Trading;
using RaidZero.Shared.Catalog;

namespace Game.Backend.Tests;

public class TraderListTests
{
    private static readonly ItemCatalog Catalog = CatalogLoader.LoadFile(TestPaths.CatalogPath);

    private static string One(string offer, double buysAt = 0.5) =>
        $$"""{ "traders": [{ "trader_id": "shop", "display_name": "Shop", "buys_at": {{buysAt.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "offers": [{{offer}}] }] }""";

    [Fact]
    public void TheCommittedTraders_FitTheCatalog()
    {
        var traders = TraderList.LoadFile(TestPaths.TradersPath, Catalog);

        var store = Assert.Single(traders.All);
        Assert.Equal("general_store", store.TraderId);
        Assert.NotEmpty(store.Offers);
        Assert.All(store.Offers, o => Assert.True(Catalog.Get(o.Item.DefId).Value < o.Price, o.OfferId + " sells for less than it's worth"));
    }

    [Fact]
    public void AnOffer_GivesAFreshItemEachTime()
    {
        var offer = TraderList.LoadFile(TestPaths.TradersPath, Catalog).Find("general_store")!.FindOffer("pistol_pm")!;

        var a = TraderList.Instantiate(Catalog, offer);
        var b = TraderList.Instantiate(Catalog, offer);

        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(a.Children[0].Id, b.Children[0].Id);
        Assert.Null(a.Slot);
        Assert.Null(offer.Item.Id); // the spec itself is untouched
    }

    [Theory]
    [InlineData("""{ "offer_id": "x", "price": 10, "item": { "def_id": "plasma_rifle" } }""", "unknown def_id 'plasma_rifle'")]
    [InlineData("""{ "offer_id": "x", "price": 10, "item": { "def_id": "currency_credits", "quantity": 100 } }""", "traders don't sell money")]
    [InlineData("""{ "offer_id": "x", "price": 0, "item": { "def_id": "medkit_basic" } }""", "price must be at least 1")]
    [InlineData("""{ "offer_id": "x", "price": 10, "stock": 2, "item": { "def_id": "medkit_basic" } }""", "needs restock_minutes")]
    [InlineData("""{ "offer_id": "X!", "price": 10, "item": { "def_id": "medkit_basic" } }""", "offer_id must be lowercase")]
    [InlineData("""{ "offer_id": "x", "price": 10, "item": { "def_id": "mag_ak_30", "children": [{ "def_id": "ammo_545_ps", "slot": "rounds", "quantity": 40 }] } }""", "invalid")]
    [InlineData("""{ "offer_id": "x", "price": 10 }, { "offer_id": "x", "price": 10, "item": { "def_id": "medkit_basic" } }""", "listed twice")]
    public void ABadOffer_StopsStartup_SayingWhy(string offer, string expected)
    {
        var e = Assert.Throws<TraderListException>(() => TraderList.Parse(One(offer), Catalog));

        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void ATraderWhoPaysNothing_StopsStartup()
    {
        var e = Assert.Throws<TraderListException>(() => TraderList.Parse(One("""{ "offer_id": "x", "price": 10, "item": { "def_id": "medkit_basic" } }""", buysAt: 0), Catalog));

        Assert.Contains("buys_at", e.Message);
    }
}
