using RaidZero.Shared.Raid;

namespace RaidZero.Raid.Tests;

public class RaidLifecycleTests
{
    // A 10-minute raid with a 60-second grace for an empty server, to keep the numbers readable.
    private static RaidLifecycle Raid() =>
        new(new RaidLifecycleOptions { Duration = TimeSpan.FromMinutes(10), EmptyGrace = TimeSpan.FromSeconds(60) });

    private static void Join(RaidLifecycle raid, string playerId, double now, bool connected = true)
    {
        Assert.True(raid.TryBeginJoin());
        raid.PlayerJoined(playerId, connected, now);
        raid.EndJoin();
    }

    [Fact]
    public void ARaid_WaitsWithItsClockStopped_UntilSomeoneJoins()
    {
        var raid = Raid();

        Assert.False(raid.Tick(5000));
        Assert.Equal(RaidPhase.Waiting, raid.Phase);
        Assert.Equal(600, raid.SecondsRemaining(5000));

        Join(raid, "sam", 5000);

        Assert.Equal((RaidPhase.InProgress, 5000.0), (raid.Phase, raid.StartedAt!.Value));
        Assert.Equal(450, raid.SecondsRemaining(5150));
    }

    [Fact]
    public void WhenTheClockRunsOut_EveryoneStillInsideIsMissing()
    {
        var raid = Raid();
        Join(raid, "sam", 0);
        Join(raid, "ann", 30);
        raid.PlayerLeft("ann");

        Assert.False(raid.Tick(599.9));
        Assert.True(raid.Tick(600));

        Assert.Equal((RaidPhase.Ending, RaidEndReason.TimeUp), (raid.Phase, raid.EndReason));
        Assert.Equal(new[] { "sam" }, raid.PlayersInside);
        Assert.Equal(0, raid.SecondsRemaining(600));
        Assert.False(raid.Tick(601)); // it only ends once
    }

    [Fact]
    public void ARaid_EndsEarly_OnceEveryoneWhoJoinedHasExtractedOrDied()
    {
        var raid = Raid();
        Join(raid, "sam", 0);
        Join(raid, "ann", 10);

        raid.PlayerLeft("sam");
        Assert.False(raid.Tick(100));
        raid.PlayerLeft("ann");

        Assert.True(raid.Tick(101));
        Assert.Equal(RaidEndReason.EveryoneLeft, raid.EndReason);
        Assert.Empty(raid.PlayersInside);
    }

    [Fact]
    public void ARaid_DoesntEndEarly_WhileSomeonesJoinIsStillWithTheBackend()
    {
        var raid = Raid();
        Join(raid, "sam", 0);
        raid.PlayerLeft("sam");
        Assert.True(raid.TryBeginJoin()); // Ann's ticket is being redeemed

        Assert.False(raid.Tick(50));

        raid.PlayerJoined("ann", connected: true, 51);
        raid.EndJoin();
        Assert.False(raid.Tick(52));
        Assert.Equal(new[] { "ann" }, raid.PlayersInside);
    }

    [Fact]
    public void AJoin_ThatFails_LetsTheRaidEndAfterAll()
    {
        var raid = Raid();
        Join(raid, "sam", 0);
        raid.PlayerLeft("sam");
        Assert.True(raid.TryBeginJoin());
        Assert.False(raid.Tick(50));

        raid.EndJoin(); // the backend refused Ann's ticket

        Assert.True(raid.Tick(51));
        Assert.Equal(RaidEndReason.EveryoneLeft, raid.EndReason);
    }

    [Fact]
    public void ARaid_NobodyIsConnectedTo_EndsAfterTheGracePeriod()
    {
        var raid = Raid();
        Join(raid, "sam", 0);
        Join(raid, "ann", 5);
        raid.PlayerDisconnected("sam");
        raid.PlayerDisconnected("ann");

        Assert.False(raid.Tick(100));
        Assert.False(raid.Tick(159));
        Assert.True(raid.Tick(160));

        Assert.Equal(RaidEndReason.Abandoned, raid.EndReason);
        Assert.Equal(new[] { "ann", "sam" }, raid.PlayersInside.OrderBy(p => p));
    }

    [Fact]
    public void Reconnecting_ResetsTheGracePeriod()
    {
        var raid = Raid();
        Join(raid, "sam", 0);
        raid.PlayerDisconnected("sam");
        Assert.False(raid.Tick(10));

        Join(raid, "sam", 50); // back with the same ticket
        Assert.False(raid.Tick(70));
        raid.PlayerDisconnected("sam");
        Assert.False(raid.Tick(80));

        Assert.False(raid.Tick(139));
        Assert.True(raid.Tick(140));
    }

    [Fact]
    public void AJoin_WhoseConnectionDroppedMeanwhile_CountsAsInsideButNotConnected()
    {
        var raid = Raid();
        Join(raid, "sam", 0, connected: false);

        Assert.Equal(RaidPhase.InProgress, raid.Phase);
        Assert.True(raid.IsInside("sam"));
        Assert.False(raid.Tick(0)); // the server ticks every frame, so it sees the empty raid at once
        Assert.False(raid.Tick(59));
        Assert.True(raid.Tick(60));
        Assert.Equal(RaidEndReason.Abandoned, raid.EndReason);
    }

    [Fact]
    public void APlayerWhoLeft_CantComeBackIn()
    {
        var raid = Raid();
        Join(raid, "sam", 0);
        Join(raid, "ann", 0);
        raid.PlayerLeft("sam");

        Join(raid, "sam", 10);

        Assert.False(raid.IsInside("sam"));
        Assert.Equal(new[] { "ann" }, raid.PlayersInside);
    }

    [Fact]
    public void OnceEnding_NoOneElseCanJoin_AndClosingIsFinal()
    {
        var raid = Raid();
        Join(raid, "sam", 0);
        raid.Tick(600);

        Assert.False(raid.TryBeginJoin());
        raid.MarkClosed();

        Assert.Equal(RaidPhase.Closed, raid.Phase);
        Assert.False(raid.TryBeginJoin());
        Assert.False(raid.Tick(700));
        Assert.Throws<InvalidOperationException>(raid.MarkClosed);
    }

    [Fact]
    public void ClosingARaidThatHasntEnded_IsAProgrammingError()
    {
        var raid = Raid();
        Join(raid, "sam", 0);

        Assert.Throws<InvalidOperationException>(raid.MarkClosed);
    }

    [Fact]
    public void AnAbort_EndsTheRaidAtOnce_EvenBeforeAnyoneJoined()
    {
        var waiting = Raid();
        waiting.End(RaidEndReason.Aborted, 30);

        var running = Raid();
        Join(running, "sam", 0);
        running.End(RaidEndReason.Aborted, 30);
        running.End(RaidEndReason.TimeUp, 31); // a second end changes nothing

        Assert.Equal((RaidPhase.Ending, RaidEndReason.Aborted), (waiting.Phase, waiting.EndReason));
        Assert.Equal((RaidPhase.Ending, RaidEndReason.Aborted), (running.Phase, running.EndReason));
        Assert.Equal(new[] { "sam" }, running.PlayersInside);
    }
}
