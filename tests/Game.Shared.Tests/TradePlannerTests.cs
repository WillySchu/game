using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Trading;
using static Game.Shared.Tests.TestCatalog;

namespace Game.Shared.Tests;

public class TradePlannerTests
{
    private const double Half = 0.5;

    // The test catalog with values, and money that stacks to 1000 so overflowing a stack is easy to test.
    private static readonly ItemCatalog Catalog = BuildCatalog();

    private static ItemCatalog BuildCatalog()
    {
        var definitions = Definitions();
        var values = new Dictionary<string, int> { ["rifle"] = 30000, ["mag_30"] = 2000, ["ammo"] = 100, ["gpu"] = 50000, ["backpack"] = 5000 };
        foreach (var definition in definitions)
        {
            definition.Value = values.GetValueOrDefault(definition.DefId);
        }

        definitions.Add(new ItemDefinition { DefId = WellKnownDefs.Credits, MaxStack = 1000, Value = 1, Tags = ["currency"] });
        return new ItemCatalog(definitions);
    }

    private static ItemNode Money(int quantity, int x, int y = 9) => Node(WellKnownDefs.Credits, "grid", x, y, quantity);

    private static ItemNode LoadedRifle(double durability = 1.0, string? slot = "grid", int? x = 0, int? y = 0) =>
        Node("rifle", slot, x, y, durability: durability, children: [Node("mag_30", "magazine", children: [Node("ammo", "rounds", quantity: 30)])]);

    [Fact]
    public void SellPrice_IsAShareOfEverythingInside_LessWhenWorn()
    {
        var rifle = LoadedRifle();

        Assert.Equal((30000 + 2000 + 30 * 100) / 2, TraderPrices.SellPrice(Catalog, rifle, Half));

        rifle.Durability = 0.0;
        Assert.Equal((long)((30000 * TraderPrices.WornShare + 2000 + 3000) / 2), TraderPrices.SellPrice(Catalog, rifle, Half));

        rifle.Durability = 0.5;
        Assert.Equal((long)((30000 * 0.625 + 5000) / 2), TraderPrices.SellPrice(Catalog, rifle, Half));
    }

    [Fact]
    public void Sale_RemovesTheItemAndWhatsInIt_AndTopsUpTheMoney()
    {
        var rifle = LoadedRifle();
        var stash = Node("stash", children: [rifle, Money(100, 9)]);

        var plan = TradePlanner.PlanSale(Catalog, stash, null, [rifle.Id], Half);

        Assert.True(plan.Success, plan.ToString());
        Assert.Equal(17500, plan.Credits);
        Assert.Equal(rifle.Walk().Select(n => n.Id), plan.Sold);
        Assert.DoesNotContain(rifle, stash.Children);
        // 100 + 17500 in stacks of 1000: the old stack fills up, and 17 new ones (the last of 600) hold the rest.
        Assert.Single(plan.Updated);
        Assert.Equal(1000, plan.Updated[0].Quantity);
        Assert.Equal(17, plan.Added.Count);
        Assert.Equal(600, plan.Added[^1].Quantity);
        Assert.Equal(17600, TradePlanner.Money(stash));
    }

    [Fact]
    public void Sale_CanSellEquippedGear_AndPaysIntoTheStash()
    {
        var rifle = LoadedRifle(slot: "primary", x: null, y: null);
        var character = Node("character", children: [rifle]);
        var stash = Node("stash");

        var plan = TradePlanner.PlanSale(Catalog, stash, character, [rifle.Id], Half);

        Assert.True(plan.Success, plan.ToString());
        Assert.Empty(character.Children);
        Assert.Equal(17500, TradePlanner.Money(stash));
    }

    [Fact]
    public void Sale_FreesTheSpaceTheMoneyNeeds()
    {
        // A stash exactly full of rifles has no room for a new stack until one is sold.
        var rifles = Enumerable.Range(0, 10).Select(i => LoadedRifle(x: (i % 2) * 4, y: (i / 2) * 2)).ToList();
        var stash = Node("stash", children: rifles.Concat([Node("ammo", "grid", 8, 0, quantity: 1)]).ToArray());
        for (int y = 0; y < 10; y++)
        {
            for (int x = 8; x < 10; x++)
            {
                if (!(x == 8 && y == 0))
                {
                    stash.Children.Add(Node("fuse", "grid", x, y));
                }
            }
        }

        // At a tenth, the rifle fetches 3500: four stacks, in the eight cells it leaves.
        var plan = TradePlanner.PlanSale(Catalog, stash, null, [rifles[3].Id], 0.1);

        Assert.True(plan.Success, plan.ToString());
        Assert.Equal(3500, TradePlanner.Money(stash));
    }

    [Fact]
    public void Sale_WithNoRoomForTheMoney_IsRefused()
    {
        // A full stash: selling the loaded magazine in the corner frees one cell, but its 5000 credits need five stacks.
        var mag = Node("mag_30", "grid", 0, 0, children: [Node("ammo", "rounds", quantity: 30)]);
        var stash = Node("stash", children: [mag]);
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                if (x > 0 || y > 1)
                {
                    stash.Children.Add(Node("fuse", "grid", x, y));
                }
            }
        }

        var plan = TradePlanner.PlanSale(Catalog, stash, null, [mag.Id], 1.0);

        Assert.False(plan.Success);
        Assert.Equal(TradeErrorCodes.NoSpace, plan.Error);
    }

    [Theory]
    [InlineData("money", TradeErrorCodes.NotForSale)]
    [InlineData("fuse", TradeErrorCodes.NotForSale)]
    [InlineData("stash", TradeErrorCodes.NotForSale)]
    [InlineData("backpack_with_money", TradeErrorCodes.MoneyInside)]
    [InlineData("twice", TradeErrorCodes.ListedTwice)]
    [InlineData("mag_and_rifle", TradeErrorCodes.ListedTwice)]
    [InlineData("missing", TradeErrorCodes.ItemNotFound)]
    [InlineData("nothing", TradeErrorCodes.InvalidCount)]
    public void Sale_OfThingsTheTraderWontTake_IsRefused(string what, string error)
    {
        var rifle = LoadedRifle();
        var money = Money(500, 9);
        var fuse = Node("fuse", "grid", 8, 0);
        var backpack = Node("backpack", "grid", 4, 0, children: [Node(WellKnownDefs.Credits, "grid", 0, 0, 50)]);
        var stash = Node("stash", children: [rifle, money, fuse, backpack]);
        var mag = rifle.Children[0];
        string[] ids = what switch
        {
            "money" => [money.Id],
            "fuse" => [fuse.Id],
            "stash" => [stash.Id],
            "backpack_with_money" => [backpack.Id],
            "twice" => [rifle.Id, rifle.Id],
            "mag_and_rifle" => [mag.Id, rifle.Id],
            "missing" => ["0190f1a2-0000-7000-8000-000000000000"],
            _ => [],
        };

        var plan = TradePlanner.PlanSale(Catalog, stash, null, ids, Half);

        Assert.False(plan.Success);
        Assert.Equal(error, plan.Error);
        Assert.False(string.IsNullOrEmpty(plan.Message));
    }

    [Fact]
    public void Purchase_PaysFromTheSmallestStacksFirst_AndPlacesTheItem()
    {
        var big = Money(900, 0);
        var small = Money(150, 1);
        var inBackpack = Node(WellKnownDefs.Credits, "grid", 0, 0, 200);
        var stash = Node("stash", children: [big, small, Node("backpack", "grid", 4, 0, children: [inBackpack])]);
        var rifle = LoadedRifle(x: null, y: null);

        var plan = TradePlanner.PlanPurchase(Catalog, stash, [rifle], 500);

        Assert.True(plan.Success, plan.ToString());
        Assert.Equal(500, plan.Credits);
        Assert.Equal(new[] { small.Id, inBackpack.Id }, plan.Spent);
        Assert.Equal(new[] { big.Id }, plan.Updated.Select(n => n.Id));
        Assert.Equal(750, big.Quantity);
        Assert.Equal(750, TradePlanner.Money(stash));
        Assert.Same(rifle, Assert.Single(plan.Added));
        Assert.Equal((SlotNames.Grid, 0, 0), (rifle.Slot, rifle.X, rifle.Y)); // where the small stack was
        Assert.Contains(rifle, stash.Children);
    }

    [Fact]
    public void Purchase_ExactlyEverything_LeavesNoMoney()
    {
        var stash = Node("stash", children: [Money(300, 0), Money(200, 1)]);

        var plan = TradePlanner.PlanPurchase(Catalog, stash, [Node("gpu")], 500);

        Assert.True(plan.Success, plan.ToString());
        Assert.Equal(2, plan.Spent.Count);
        Assert.Empty(plan.Updated);
        Assert.Equal(0, TradePlanner.Money(stash));
    }

    [Fact]
    public void Purchase_WithoutEnoughMoney_IsRefused()
    {
        var stash = Node("stash", children: [Money(499, 0)]);

        var plan = TradePlanner.PlanPurchase(Catalog, stash, [Node("gpu")], 500);

        Assert.False(plan.Success);
        Assert.Equal(TradeErrorCodes.NotEnoughMoney, plan.Error);
        Assert.Contains("499", plan.Message);
    }

    [Fact]
    public void Purchase_OfSeveral_PlacesEach_OrSaysWhenTheyDontFit()
    {
        var roomy = Node("stash", children: [Money(1000, 9)]);
        var fits = TradePlanner.PlanPurchase(Catalog, roomy, Enumerable.Range(0, 5).Select(_ => Node("gpu")).ToList(), 100);
        Assert.True(fits.Success, fits.ToString());
        Assert.Equal(5, fits.Added.Select(a => (a.X, a.Y)).Distinct().Count());

        // Only nine cells free, in a row: four 2x1 cards fit, a fifth doesn't.
        var tight = Node("stash", children: [Money(1000, 9), Node("fuse", "grid", 9, 0)]);
        for (int y = 1; y < 10; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                if (!(x == 9 && y == 9))
                {
                    tight.Children.Add(Node("fuse", "grid", x, y));
                }
            }
        }

        var tooMany = TradePlanner.PlanPurchase(Catalog, tight, Enumerable.Range(0, 5).Select(_ => Node("gpu")).ToList(), 100);
        Assert.False(tooMany.Success);
        Assert.Equal(TradeErrorCodes.NoSpace, tooMany.Error);
    }
}
