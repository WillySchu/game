using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Loot;
using static Game.Shared.Tests.TestCatalog;

namespace Game.Shared.Tests;

public class LootTests
{
    private readonly ItemCatalog _catalog = Build();
    private static string NewId() => Uuid7.NewId();

    private static LootTable Table(params LootEntry[] entries) => new() { Name = "test", Entries = [.. entries] };

    private static LootEntry Item(string defId, int weight = 1) => new() { DefId = defId, Weight = weight };

    private List<ItemNode> Roll(LootTable table, ulong seed) => LootRoller.Roll(_catalog, table, new LootRandom(seed), NewId);

    private static string Describe(ItemNode container) =>
        string.Join(";", container.Walk().Skip(1).Select(i => $"{i.DefId}x{i.Quantity}@{i.Slot}{i.X},{i.Y}{(i.Rotated ? "r" : "")}:{i.Durability}"));

    // ---- the generator ----

    [Fact]
    public void Random_IsSplitMix64_SoEveryRuntimeAgrees()
    {
        // The reference sequence for seed 0. If this changes, every recorded raid seed means something else.
        var random = new LootRandom(0);

        Assert.Equal(0xE220A8397B1DCDAFUL, random.NextULong());
        Assert.Equal(0x6E789E6AA1B965F4UL, random.NextULong());
    }

    [Fact]
    public void Random_RangesStayInside()
    {
        var random = new LootRandom(7);
        for (int i = 0; i < 1000; i++)
        {
            Assert.InRange(random.Range(3, 5), 3, 5);
            Assert.InRange(random.NextDouble(), 0, 0.9999999999);
        }

        Assert.Equal(4, random.Range(4, 4));
    }

    // ---- rolling ----

    private LootTable Mixed() => Table(Item("gpu"), Item("fuse"), new LootEntry { DefId = "ammo", MinQuantity = 1, MaxQuantity = 60 }, Item("rifle"));

    [Fact]
    public void TheSameSeedAndContainer_GiveTheSameLoot()
    {
        var table = Mixed();
        table.MinRolls = 2;
        table.MaxRolls = 5;

        var first = LootRoller.RollContainer(_catalog, "crate-1", "stash", table, 42, NewId);
        var again = LootRoller.RollContainer(_catalog, "crate-1", "stash", table, 42, NewId);

        Assert.NotEmpty(first.Children);
        Assert.Equal(Describe(first), Describe(again));
    }

    [Fact]
    public void OtherSeedsAndContainers_GiveOtherLoot()
    {
        var table = Mixed();
        table.MinRolls = 2;
        table.MaxRolls = 5;
        var reference = Describe(LootRoller.RollContainer(_catalog, "crate-1", "stash", table, 42, NewId));

        var otherSeeds = Enumerable.Range(43, 10).Select(s => Describe(LootRoller.RollContainer(_catalog, "crate-1", "stash", table, s, NewId)));
        var otherCrates = Enumerable.Range(2, 10).Select(c => Describe(LootRoller.RollContainer(_catalog, "crate-" + c, "stash", table, 42, NewId)));

        Assert.Contains(otherSeeds, d => d != reference);
        Assert.Contains(otherCrates, d => d != reference);
    }

    [Fact]
    public void RollCount_IsBetweenTheMinimumAndMaximum_AllOfThem()
    {
        var table = Table(Item("fuse"));
        table.MinRolls = 2;
        table.MaxRolls = 4;

        var counts = Enumerable.Range(0, 200).Select(s => Roll(table, (ulong)s).Count).ToList();

        Assert.All(counts, c => Assert.InRange(c, 2, 4));
        Assert.Equal([2, 3, 4], counts.Distinct().Order());
    }

    [Fact]
    public void Weights_SetTheOdds_IncludingNothing()
    {
        var table = Table(Item("gpu", weight: 3), Item("fuse", weight: 1));
        table.NothingWeight = 4; // half the picks find nothing; of the rest, three in four are a gpu

        var picks = Enumerable.Range(0, 4000).Select(s => Roll(table, (ulong)s).SingleOrDefault()?.DefId).ToList();

        Assert.InRange(picks.Count(p => p == null) / 4000.0, 0.46, 0.54);
        Assert.InRange(picks.Count(p => p == "gpu") / 4000.0, 0.34, 0.41);
        Assert.InRange(picks.Count(p => p == "fuse") / 4000.0, 0.10, 0.15);
    }

    [Fact]
    public void AnEntryWithNoWeight_NeverTurnsUp()
    {
        var table = Table(Item("gpu", weight: 0), Item("fuse"));

        Assert.All(Enumerable.Range(0, 200), s => Assert.Equal("fuse", Roll(table, (ulong)s).Single().DefId));
    }

    [Fact]
    public void Stacks_GetAQuantityInTheRange_AndOtherItemsOne()
    {
        var table = Table(new LootEntry { DefId = "ammo", MinQuantity = 10, MaxQuantity = 20 }, new LootEntry { DefId = "fuse", MinQuantity = 5, MaxQuantity = 5 });
        table.MinRolls = table.MaxRolls = 2;

        var items = Enumerable.Range(0, 200).SelectMany(s => Roll(table, (ulong)s)).ToList();

        Assert.All(items.Where(i => i.DefId == "ammo"), i => Assert.InRange(i.Quantity, 10, 20));
        Assert.All(items.Where(i => i.DefId == "fuse"), i => Assert.Equal(1, i.Quantity)); // it doesn't stack
    }

    [Fact]
    public void Durability_IsInTheRange_ForItemsThatHaveIt()
    {
        var table = Table(new LootEntry { DefId = "rifle", MinDurability = 0.3, MaxDurability = 0.6 }, Item("gpu"));
        table.MinRolls = table.MaxRolls = 2;

        var items = Enumerable.Range(0, 100).SelectMany(s => Roll(table, (ulong)s)).ToList();

        Assert.All(items.Where(i => i.DefId == "rifle"), i =>
        {
            Assert.InRange(i.Durability!.Value, 0.3, 0.6);
            Assert.Equal(Math.Round(i.Durability.Value, 2), i.Durability.Value);
        });
        Assert.All(items.Where(i => i.DefId == "gpu"), i => Assert.Null(i.Durability));
    }

    [Fact]
    public void ALoadedWeapon_HasAMagazineWithRounds()
    {
        var table = Table(new LootEntry { DefId = "rifle", LoadedChance = 1, MinFill = 0.5, MaxFill = 0.5 });

        var rifle = Roll(table, 1).Single();

        var magazine = Assert.Single(rifle.Children);
        Assert.Equal(("mag_30", "magazine"), (magazine.DefId, magazine.Slot));
        var rounds = Assert.Single(magazine.Children);
        Assert.Equal(("ammo", "rounds", 15), (rounds.DefId, rounds.Slot, rounds.Quantity));
        Assert.Empty(InventoryValidator.Validate(_catalog, new ItemNode { Id = NewId(), DefId = "stash", Children = [Place(rifle)] }));
    }

    private static ItemNode Place(ItemNode item)
    {
        item.Slot = "grid";
        item.X = 0;
        item.Y = 0;
        return item;
    }

    [Fact]
    public void LoadedChance_DecidesHowOften()
    {
        var table = Table(new LootEntry { DefId = "mag_30", LoadedChance = 0.25 });

        var loaded = Enumerable.Range(0, 2000).Count(s => Roll(table, (ulong)s).Single().Children.Count > 0);

        Assert.InRange(loaded / 2000.0, 0.21, 0.29);
        Assert.Empty(Roll(Table(Item("rifle")), 1).Single().Children); // 0: never loaded
    }

    [Fact]
    public void AnEmptyFill_LeavesTheMagazineEmpty()
    {
        var table = Table(new LootEntry { DefId = "rifle", LoadedChance = 1, MinFill = 0, MaxFill = 0 });

        var magazine = Assert.Single(Roll(table, 1).Single().Children);

        Assert.Empty(magazine.Children);
    }

    [Fact]
    public void ATableEntry_RollsThatTable()
    {
        var inner = Table(Item("fuse"));
        inner.Name = "inner";
        inner.MinRolls = inner.MaxRolls = 3;
        var outer = Table(new LootEntry { Table = inner });

        var items = Roll(outer, 1);

        Assert.Equal(["fuse", "fuse", "fuse"], items.Select(i => i.DefId));
    }

    // ---- packing ----

    [Fact]
    public void Packing_PutsTheBiggestFirst_AndLeavesOutWhatDoesntFit()
    {
        // A 4x3 backpack: the 4x2 rifle, the 2x1 gpu and two of the four fuses fit, but only if the rifle goes first.
        var container = new ItemNode { Id = NewId(), DefId = "backpack" };
        var items = new[] { "fuse", "fuse", "fuse", "fuse", "gpu", "rifle" }
            .Select(d => new ItemNode { Id = NewId(), DefId = d, Durability = d == "rifle" ? 1.0 : null }).ToList();
        var leftOut = new List<ItemNode>();

        var packed = LootRoller.Pack(_catalog, container, items, leftOut);

        Assert.Equal(4, packed);
        Assert.Equal(["fuse", "fuse"], leftOut.Select(i => i.DefId));
        Assert.Contains(container.Children, i => i.DefId == "rifle" && i.X == 0 && i.Y == 0);
        Assert.Empty(InventoryValidator.Validate(_catalog, container));
    }

    // ---- checking tables ----

    [Fact]
    public void AGoodTable_HasNoProblems()
    {
        var inner = Table(new LootEntry { DefId = "ammo", MinQuantity = 10, MaxQuantity = 60 });
        var table = Table(
            new LootEntry { DefId = "rifle", MinDurability = 0.5, MaxDurability = 1, LoadedChance = 0.5, MinFill = 0, MaxFill = 1 },
            new LootEntry { Table = inner, Weight = 2 });
        table.MinRolls = 0;
        table.MaxRolls = 3;
        table.NothingWeight = 1;

        Assert.Empty(LootRoller.Validate(_catalog, table));
    }

    [Theory]
    [InlineData("no_such_item", 1, 1, "there's no item 'no_such_item'")]
    [InlineData("ammo", 0, 10, "the quantity goes from 0 to 10")]
    [InlineData("ammo", 20, 10, "the quantity goes from 20 to 10")]
    [InlineData("ammo", 1, 61, "a stack holds 60")]
    [InlineData("fuse", 1, 2, "it doesn't stack")]
    public void BadItemsAndQuantities_AreReported(string defId, int min, int max, string expected)
    {
        var errors = LootRoller.Validate(_catalog, Table(new LootEntry { DefId = defId, MinQuantity = min, MaxQuantity = max }));

        Assert.Contains(expected, Assert.Single(errors));
    }

    [Fact]
    public void BadRangesAndShapes_AreReported()
    {
        var table = Table(
            new LootEntry { DefId = "rifle", MinDurability = 0.8, MaxDurability = 0.2 },
            new LootEntry { DefId = "gpu", LoadedChance = 0.5 },
            new LootEntry { DefId = "mag_30", LoadedChance = 1.5 },
            new LootEntry { DefId = "fuse", Table = Table(Item("fuse")) },
            new LootEntry(),
            new LootEntry { DefId = "fuse", Weight = -1 });
        table.MinRolls = 3;
        table.MaxRolls = 1;
        table.NothingWeight = -2;

        var errors = LootRoller.Validate(_catalog, table);

        Assert.Contains(errors, e => e.Contains("rolls go from 3 to 1"));
        Assert.Contains(errors, e => e.Contains("nothing weight can't be negative"));
        Assert.Contains(errors, e => e.Contains("entry 1") && e.Contains("durability"));
        Assert.Contains(errors, e => e.Contains("entry 2") && e.Contains("nothing to load"));
        Assert.Contains(errors, e => e.Contains("entry 3") && e.Contains("loaded chance goes from 0 to 1"));
        Assert.Contains(errors, e => e.Contains("entry 4") && e.Contains("not both"));
        Assert.Contains(errors, e => e.Contains("entry 5") && e.Contains("set an item or a table."));
        Assert.Contains(errors, e => e.Contains("entry 6") && e.Contains("weight can't be negative"));
    }

    [Fact]
    public void ATableWithRollsButNothingToPick_IsReported()
    {
        var errors = LootRoller.Validate(_catalog, Table(Item("fuse", weight: 0)));

        Assert.Contains("no entries with any weight", Assert.Single(errors));
    }

    [Fact]
    public void TablesThatIncludeThemselves_AreReported_AndStillRollSafely()
    {
        var a = Table(Item("fuse"));
        a.Name = "A";
        var b = Table(new LootEntry { Table = a });
        b.Name = "B";
        a.Entries.Add(new LootEntry { Table = b });
        a.MinRolls = a.MaxRolls = 2;

        Assert.Contains("A > B includes A", Assert.Single(LootRoller.Validate(_catalog, a)));
        Assert.All(Roll(a, 3), i => Assert.Equal("fuse", i.DefId)); // the depth limit stops it
    }

    // ---- simulating ----

    [Fact]
    public void Simulation_GivesTheOdds()
    {
        var table = Table(Item("fuse"), new LootEntry { DefId = "ammo", MinQuantity = 10, MaxQuantity = 30 });
        table.NothingWeight = 2;

        var sim = LootSimulation.Run(_catalog, table, "backpack", 4000);

        Assert.Equal(4000, sim.Runs);
        Assert.InRange(sim.EmptyChance, 0.46, 0.54);
        Assert.InRange(sim.AverageItems, 0.46, 0.54);
        Assert.Equal(0, sim.AverageLeftOut);
        Assert.Equal(["ammo", "fuse"], sim.Items.Select(o => o.DefId).Order());
        var ammo = sim.Items.Single(o => o.DefId == "ammo");
        Assert.InRange(ammo.Chance, 0.22, 0.28);
        Assert.InRange(ammo.AverageQuantity, 18, 22);
    }

    [Fact]
    public void Simulation_CountsWhatDoesntFit()
    {
        var table = Table(Item("rifle"));
        table.MinRolls = table.MaxRolls = 3; // a 4x3 backpack takes one 4x2 rifle

        var sim = LootSimulation.Run(_catalog, table, "backpack", 50);

        Assert.Equal((1.0, 2.0), (sim.AverageItems, sim.AverageLeftOut));
    }
}
