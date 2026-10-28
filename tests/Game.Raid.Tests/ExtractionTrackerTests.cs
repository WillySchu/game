namespace RaidZero.Raid.Tests;

public class ExtractionTrackerTests
{
    [Fact]
    public void StayingInAZone_ForItsWholeCountdown_Extracts()
    {
        var tracker = new ExtractionTracker();

        Assert.Equal(ExtractionChange.Started, tracker.Update("sam", "north_gate", 8, 100));
        Assert.Equal(ExtractionChange.None, tracker.Update("sam", "north_gate", 8, 107.9));
        Assert.Equal(3, tracker.SecondsLeft("sam", 105)!.Value, precision: 6);
        Assert.Equal(ExtractionChange.Completed, tracker.Update("sam", "north_gate", 8, 108));

        Assert.Null(tracker.ZoneOf("sam"));
        Assert.Null(tracker.SecondsLeft("sam", 108));
    }

    [Fact]
    public void Leaving_CancelsTheCountdown_AndComingBackStartsItOver()
    {
        var tracker = new ExtractionTracker();
        tracker.Update("sam", "north_gate", 8, 100);

        Assert.Equal(ExtractionChange.Cancelled, tracker.Update("sam", null, 8, 105));
        Assert.Equal(ExtractionChange.None, tracker.Update("sam", null, 8, 106));
        Assert.Equal(ExtractionChange.Started, tracker.Update("sam", "north_gate", 8, 107));
        Assert.Equal(ExtractionChange.None, tracker.Update("sam", "north_gate", 8, 114));
        Assert.Equal(ExtractionChange.Completed, tracker.Update("sam", "north_gate", 8, 115));
    }

    [Fact]
    public void MovingToAnotherZone_StartsThatZonesCountdown()
    {
        var tracker = new ExtractionTracker();
        tracker.Update("sam", "north_gate", 8, 100);

        Assert.Equal(ExtractionChange.Started, tracker.Update("sam", "boat_dock", 12, 104));

        Assert.Equal("boat_dock", tracker.ZoneOf("sam"));
        Assert.Equal(ExtractionChange.None, tracker.Update("sam", "boat_dock", 12, 112));
        Assert.Equal(ExtractionChange.Completed, tracker.Update("sam", "boat_dock", 12, 116));
    }

    [Fact]
    public void AnInterruption_StartsTheCountdownOver()
    {
        var tracker = new ExtractionTracker();
        tracker.Update("sam", "north_gate", 8, 100);

        Assert.True(tracker.Interrupt("sam", 106));
        Assert.False(tracker.Interrupt("ann", 106)); // not extracting, nothing to interrupt

        Assert.Equal(ExtractionChange.None, tracker.Update("sam", "north_gate", 8, 108));
        Assert.Equal(ExtractionChange.Completed, tracker.Update("sam", "north_gate", 8, 114));
    }

    [Fact]
    public void PlayersCountDownSeparately_AndForgettingOneLeavesTheOthers()
    {
        var tracker = new ExtractionTracker();
        tracker.Update("sam", "north_gate", 8, 100);
        tracker.Update("ann", "north_gate", 8, 103);

        tracker.Forget("sam");

        Assert.Null(tracker.ZoneOf("sam"));
        Assert.Equal(ExtractionChange.None, tracker.Update("sam", null, 8, 104)); // forgotten, so nothing to cancel
        Assert.Equal(ExtractionChange.Completed, tracker.Update("ann", "north_gate", 8, 111));
    }
}
