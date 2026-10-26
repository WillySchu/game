using RaidZero.Shared.Catalog;

namespace Game.Shared.Tests;

public class CatalogTests
{
    [Fact]
    public void ValidDefinitions_BuildACatalog()
    {
        var catalog = TestCatalog.Build();

        Assert.Equal(TestCatalog.Definitions().Count, catalog.Count);
        Assert.True(catalog.TryGet("rifle", out var rifle));
        Assert.Equal(4, rifle.Width);
        Assert.False(catalog.Contains("nope"));
        Assert.Throws<KeyNotFoundException>(() => catalog.Get("nope"));
    }

    [Fact]
    public void InvalidDefinitions_ReportEveryProblemAtOnce()
    {
        var defs = TestCatalog.Definitions();
        defs.Add(new ItemDefinition { DefId = "rifle" });
        defs.Add(new ItemDefinition { DefId = "Bad-Name" });
        defs.Add(new ItemDefinition { DefId = "zero_size", Width = 0 });
        defs.Add(new ItemDefinition { DefId = "stack_with_durability", MaxStack = 5, HasDurability = true });
        defs.Add(new ItemDefinition { DefId = "reserved_slot", Slots = [new() { Name = "grid", Accepts = ["x"] }] });
        defs.Add(new ItemDefinition { DefId = "empty_slot", Slots = [new() { Name = "thing" }] });

        var ex = Assert.Throws<CatalogException>(() => new ItemCatalog(defs));

        Assert.Contains(ex.Errors, e => e.StartsWith("rifle: duplicate", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad-Name: def_id", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("zero_size: width", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("stack_with_durability: stackable", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("reserved_slot: slot name 'grid'", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("empty_slot: slot 'thing' accepts nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void Hash_IgnoresDisplayNamesAndOrdering()
    {
        var original = TestCatalog.Definitions();
        var reordered = TestCatalog.Definitions();
        reordered.Reverse();
        reordered[0].DisplayName = "Renamed";
        reordered.Single(d => d.DefId == "character").Slots.Reverse();

        Assert.Equal(CatalogHash.Compute(original), CatalogHash.Compute(reordered));
    }

    [Fact]
    public void Hash_ChangesWhenARuleChanges()
    {
        var original = TestCatalog.Definitions();
        var changed = TestCatalog.Definitions();
        changed.Single(d => d.DefId == "ammo").MaxStack = 61;

        Assert.NotEqual(CatalogHash.Compute(original), CatalogHash.Compute(changed));
    }

    [Fact]
    public void Hash_IsSixteenLowercaseHexDigits()
    {
        string hash = CatalogHash.Compute(TestCatalog.Definitions());

        Assert.Matches("^[0-9a-f]{16}$", hash);
        Assert.Equal(hash, TestCatalog.Build().Version);
    }
}
