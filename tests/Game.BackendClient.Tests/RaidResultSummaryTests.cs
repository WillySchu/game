using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi.Tests;

/// <summary>What the Hideout's result panel says about a finished raid.</summary>
public sealed class RaidResultSummaryTests
{
    private const string RaidId = "0192f0a0-0000-7000-8000-000000000001";

    private static readonly ItemCatalog Catalog = new(
    [
        new ItemDefinition { DefId = "rifle", DisplayName = "AK Rifle", Width = 4, Height = 2 },
        new ItemDefinition { DefId = "mag", DisplayName = "AK 30-round magazine", Height = 2 },
        new ItemDefinition { DefId = "ammo", DisplayName = "5.45x39 PS", MaxStack = 60 },
        new ItemDefinition { DefId = "gpu", DisplayName = "Graphics card", Width = 2 },
        new ItemDefinition { DefId = "nameless" },
    ]);

    private static ItemNode Node(string id, string defId, int quantity = 1, params ItemNode[] children) =>
        new() { Id = id, DefId = defId, Quantity = quantity, Children = children.ToList() };

    private static PlayerProfile Profile(ItemNode character, params ItemNode[] stashItems) =>
        new("player", "ci", 3, Node("stash", "stash", 1, stashItems), character, null, RaidId);

    private static RaidOutcome Outcome(string outcome, string[]? gained = null, string[]? returned = null, string[]? lost = null) =>
        new(RaidId, "greybox_01", "closed", outcome, gained ?? [], returned ?? [], lost ?? []);

    // What the player took in: a loaded rifle.
    private static ItemNode CarriedIn() =>
        Node("character", "character", 1, Node("rifle-1", "rifle", 1, Node("mag-1", "mag", 1, Node("rounds-1", "ammo", 30))));

    [Theory]
    [InlineData("extracted", "Extracted")]
    [InlineData("died", "Lost in the zone")]
    [InlineData("refunded", "Raid called off")]
    [InlineData("expired", "You never got in")]
    [InlineData("in_raid", "Still in the zone")]
    public void Title_FollowsTheOutcome(string outcome, string title) =>
        Assert.Equal(title, RaidResultSummary.Build(Outcome(outcome), Profile(Node("character", "character")), Catalog).Title);

    [Fact]
    public void BroughtHome_NamesNewLoot_CountingWhatsInsideWithItsContainer()
    {
        // A second rifle came home, loaded, plus two graphics cards and two stacks of rounds.
        var after = Profile(
            CarriedIn(),
            Node("rifle-2", "rifle", 1, Node("mag-2", "mag", 1, Node("rounds-2", "ammo", 20))),
            Node("gpu-1", "gpu"),
            Node("gpu-2", "gpu"),
            Node("rounds-3", "ammo", 60),
            Node("rounds-4", "ammo", 15));
        var outcome = Outcome("extracted", gained: ["rifle-2", "mag-2", "rounds-2", "gpu-1", "gpu-2", "rounds-3", "rounds-4"], returned: ["rifle-1", "mag-1", "rounds-1"]);

        var summary = RaidResultSummary.Build(outcome, after, Catalog, CarriedIn());

        Assert.Equal(["5.45x39 PS x75", "Graphics card x2", "AK Rifle"], summary.BroughtHome);
        Assert.Contains("new loot", summary.Sentence);
        Assert.Empty(summary.Lost);
    }

    [Fact]
    public void Lost_NamesWhatWasCarriedIn_WhenTheScreenRemembers()
    {
        var summary = RaidResultSummary.Build(Outcome("died", lost: ["rifle-1", "mag-1", "rounds-1"]), Profile(Node("character", "character")), Catalog, CarriedIn());

        Assert.Equal(["AK Rifle"], summary.Lost);
        Assert.Equal(3, summary.LostCount);
    }

    [Fact]
    public void Lost_IsOnlyACount_WhenTheScreenDoesntRemember()
    {
        var summary = RaidResultSummary.Build(Outcome("died", lost: ["rifle-1", "mag-1", "rounds-1"]), Profile(Node("character", "character")), Catalog);

        Assert.Empty(summary.Lost);
        Assert.Equal(3, summary.LostCount);
    }

    [Fact]
    public void AnExtraction_CanStillLoseWhatWasUsedUp()
    {
        // Rounds fired: the magazine came home, its rounds didn't.
        var summary = RaidResultSummary.Build(Outcome("extracted", returned: ["rifle-1", "mag-1"], lost: ["rounds-1"]), Profile(CarriedIn()), Catalog, CarriedIn());

        Assert.Equal(["5.45x39 PS x30"], summary.Lost);
        Assert.Equal("You made it out.", summary.Sentence);
    }

    [Fact]
    public void ItemsWithoutADisplayName_UseTheirDefId_AndUnknownIdsAreLeftOut()
    {
        var after = Profile(Node("character", "character"), Node("n-1", "nameless"));

        var summary = RaidResultSummary.Build(Outcome("extracted", gained: ["n-1", "not-in-the-profile"]), after, Catalog);

        Assert.Equal(["nameless"], summary.BroughtHome);
    }
}
