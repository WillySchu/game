using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi.Tests;

/// <summary>What CI's scripted client counts as a right raid result: the outcome, and where the gear ended up.</summary>
public sealed class RaidResultCheckTests
{
    private const string RaidId = "0192f0a0-0000-7000-8000-000000000001";

    // The character carries a rifle with a magazine in it, and a rig.
    private static ItemNode Character() => new()
    {
        Id = "character",
        DefId = "character",
        Children =
        [
            new ItemNode { Id = "rifle", DefId = "rifle", Slot = "primary", Children = [new ItemNode { Id = "mag", DefId = "mag", Slot = "magazine" }] },
            new ItemNode { Id = "rig", DefId = "rig", Slot = "rig" },
        ],
    };

    private static ItemNode EmptyCharacter() => new() { Id = "character", DefId = "character" };

    private static ItemNode Stash(params ItemNode[] items) => new() { Id = "stash", DefId = "stash", Children = items.ToList() };

    private static PlayerProfile Profile(ItemNode character, ItemNode stash) => new("player", "ci", 2, stash, character, null, RaidId);

    private static RaidOutcome Outcome(string outcome, string[]? returned = null, string[]? lost = null) =>
        new(RaidId, "greybox_01", "closed", outcome, [], returned ?? [], lost ?? []);

    private static readonly HashSet<string> CarriedIn = RaidResultCheck.CarriedItemIds(Profile(Character(), Stash()));

    [Fact]
    public void Carried_items_are_everything_under_the_character_but_not_the_character()
    {
        Assert.Equal(new HashSet<string> { "rifle", "mag", "rig" }, CarriedIn);
    }

    [Fact]
    public void An_extraction_that_brought_everything_home_passes()
    {
        var problem = RaidResultCheck.Problem(true, CarriedIn, Outcome("extracted", returned: ["rifle", "mag", "rig"]), Profile(Character(), Stash()));

        Assert.Null(problem);
    }

    [Fact]
    public void Gear_back_in_the_stash_counts_as_home()
    {
        var after = Profile(EmptyCharacter(), Stash(Character().Children.ToArray()));

        Assert.Null(RaidResultCheck.Problem(true, CarriedIn, Outcome("extracted", returned: ["rifle", "mag", "rig"]), after));
    }

    [Fact]
    public void A_death_that_lost_everything_passes()
    {
        var problem = RaidResultCheck.Problem(false, CarriedIn, Outcome("died", lost: ["rifle", "mag", "rig"]), Profile(EmptyCharacter(), Stash()));

        Assert.Null(problem);
    }

    [Theory]
    [InlineData(true, "died")]
    [InlineData(false, "extracted")]
    [InlineData(false, "refunded")]
    [InlineData(true, "in_raid")]
    public void The_wrong_outcome_fails(bool extracted, string outcome)
    {
        var problem = RaidResultCheck.Problem(extracted, CarriedIn, Outcome(outcome), Profile(Character(), Stash()));

        Assert.Contains(outcome, problem);
    }

    [Fact]
    public void An_extraction_that_lost_something_fails()
    {
        var problem = RaidResultCheck.Problem(true, CarriedIn, Outcome("extracted", returned: ["rifle", "rig"], lost: ["mag"]), Profile(Character(), Stash()));

        Assert.Contains("lost", problem);
    }

    [Fact]
    public void An_extraction_whose_gear_is_missing_from_the_profile_fails()
    {
        var after = Profile(new ItemNode { Id = "character", DefId = "character", Children = [new ItemNode { Id = "rig", DefId = "rig", Slot = "rig" }] }, Stash());

        var problem = RaidResultCheck.Problem(true, CarriedIn, Outcome("extracted", returned: ["rifle", "mag", "rig"]), after);

        Assert.Contains("2 of the 3", problem);
    }

    [Fact]
    public void A_death_that_returned_something_fails()
    {
        var problem = RaidResultCheck.Problem(false, CarriedIn, Outcome("died", returned: ["rig"], lost: ["rifle", "mag"]), Profile(EmptyCharacter(), Stash()));

        Assert.Contains("returned", problem);
    }

    [Fact]
    public void A_death_that_lost_nothing_fails()
    {
        var problem = RaidResultCheck.Problem(false, CarriedIn, Outcome("died"), Profile(EmptyCharacter(), Stash()));

        Assert.Contains("nothing as lost", problem);
    }

    [Fact]
    public void A_death_whose_gear_is_still_owned_fails()
    {
        var problem = RaidResultCheck.Problem(false, CarriedIn, Outcome("died", lost: ["rifle", "mag", "rig"]), Profile(Character(), Stash()));

        Assert.Contains("still in their profile", problem);
    }

    [Fact]
    public void Going_in_with_nothing_fails_because_nothing_was_tested()
    {
        var problem = RaidResultCheck.Problem(true, new HashSet<string>(), Outcome("extracted"), Profile(EmptyCharacter(), Stash()));

        Assert.Contains("no gear", problem);
    }

    [Fact]
    public void No_result_fails()
    {
        Assert.NotNull(RaidResultCheck.Problem(true, CarriedIn, null!, Profile(Character(), Stash())));
    }
}
