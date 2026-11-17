using System.Numerics;

namespace RaidZero.Raid.Tests;

/// <summary>How an AI notices people: awareness from sight, noises and what its squad saw.</summary>
public class AIPerceptionTests
{
    private static SightSample Look(string id, float distance, float angle = 0f, float speed = 3f, bool clear = true, float visibility = 1f) => new()
    {
        TargetId = id,
        Position = new Vector3(0, 0, distance),
        InLineOfSight = clear,
        Distance = distance,
        AngleOffCentre = angle,
        Speed = speed,
        Visibility = visibility,
    };

    /// <summary>Ticks sight at 10 Hz for <paramref name="seconds"/>, from <paramref name="start"/>; returns the time after.</summary>
    private static double Watch(AIPerception ai, double start, double seconds, params SightSample[] samples)
    {
        var now = start;
        for (var i = 0; i < (int)Math.Round(seconds * 10); i++)
        {
            now += 0.1;
            ai.See(now, 0.1f, samples);
        }

        return now;
    }

    [Fact]
    public void WalkingIntoView_FillsAwareness_NoticesThenAlerts()
    {
        var ai = new AIPerception();
        var now = Watch(ai, 0, 0.2, Look("p1", 10));
        Assert.InRange(ai.AwarenessOf("p1"), 0.01f, AIPerception.NoticeAt);
        Assert.Null(ai.Focus);                                       // not yet: it hasn't turned to look

        now = Watch(ai, now, 0.5, Look("p1", 10));
        Assert.Equal("p1", ai.Focus);
        Assert.False(ai.IsAlert);
        Assert.True(ai.TryGetLookPoint(now, out var look, out var why));
        Assert.Equal("noticed", why);
        Assert.Equal(new Vector3(0, 0, 10), look);

        Watch(ai, now, 3, Look("p1", 10));
        Assert.Equal(1f, ai.AwarenessOf("p1"));
        Assert.True(ai.IsAlert);
        Assert.StartsWith("alert: p1", ai.Describe(now));
    }

    [Fact]
    public void Nearer_FasterAndMoreCentral_TargetsFillFaster()
    {
        var sight = new SightSettings();
        Assert.True(AIPerception.FillRate(sight, Look("a", 4)) > AIPerception.FillRate(sight, Look("a", 40)));
        Assert.True(AIPerception.FillRate(sight, Look("a", 20, angle: 5)) > AIPerception.FillRate(sight, Look("a", 20, angle: 50)));
        Assert.True(AIPerception.FillRate(sight, Look("a", 20, speed: 7)) > AIPerception.FillRate(sight, Look("a", 20, speed: 0)));
        Assert.True(AIPerception.FillRate(sight, Look("a", 20)) > AIPerception.FillRate(sight, Look("a", 20, visibility: 0.3f)));

        // Close, dead ahead and sprinting is the best it gets: one and a half times the fill rate.
        Assert.Equal(1.5f, AIPerception.FillRate(sight, Look("a", 2, speed: 10)), 3);
    }

    [Fact]
    public void BehindIt_BeyondItsRange_OrBehindCover_ItSeesNothing()
    {
        var sight = new SightSettings { ViewDistance = 80, ViewAngle = 120 };
        Assert.Equal(0f, AIPerception.FillRate(sight, Look("a", 10, angle: 61)));
        Assert.Equal(0f, AIPerception.FillRate(sight, Look("a", 81)));
        Assert.Equal(0f, AIPerception.FillRate(sight, Look("a", 10, clear: false)));
        Assert.Equal(0f, AIPerception.FillRate(sight, Look("a", 10, visibility: 0f)));
        Assert.True(AIPerception.FillRate(sight, Look("a", 10, angle: 59)) > 0f);
    }

    [Fact]
    public void OutOfSight_AwarenessDrains_ButItRemembersWhereItSawYou()
    {
        var ai = new AIPerception(new SightSettings { DrainRate = 0.2f });
        var now = Watch(ai, 0, 3, Look("p1", 5));
        Assert.True(ai.IsAlert);

        now = Watch(ai, now, 2);                                    // gone round a corner: not sampled at all
        Assert.Equal(0.6f, ai.AwarenessOf("p1"), 3);
        now = Watch(ai, now, 1, Look("p1", 5, clear: false));      // or sampled, but blocked: the same
        Assert.Equal(0.4f, ai.AwarenessOf("p1"), 3);
        Assert.Equal("p1", ai.Focus);

        now = Watch(ai, now, 2.5);
        Assert.Equal(0f, ai.AwarenessOf("p1"));
        Assert.Null(ai.Focus);
        Assert.True(ai.TryGetLastSeen("p1", out var seen, out _));
        Assert.Equal(new Vector3(0, 0, 5), seen);

        Watch(ai, now, AIPerception.RememberSeconds + 1);
        Assert.False(ai.TryGetLastSeen("p1", out _, out _));       // and in time forgets
    }

    [Fact]
    public void ItFocusesOnWhoeverItsMostAwareOf()
    {
        var ai = new AIPerception();
        var now = Watch(ai, 0, 1, Look("far", 60), Look("near", 3));
        Assert.Equal("near", ai.Focus);
        Assert.True(ai.AwarenessOf("far") < ai.AwarenessOf("near"));
        Assert.True(ai.AwarenessOf("far") > 0f);
    }

    [Fact]
    public void AShot_MakesItLookTowardsTheSound_ForAWhile()
    {
        var ai = new AIPerception();
        Assert.False(ai.TryGetLookPoint(0, out _, out _));
        Assert.Equal("calm", ai.Describe(0));

        ai.Hear(1, new Vector3(30, 0, 0));
        Assert.True(ai.TryGetLookPoint(2, out var look, out var why));
        Assert.Equal("heard", why);
        Assert.Equal(new Vector3(30, 0, 0), look);
        Assert.Equal("heard a shot 3 s ago", ai.Describe(4));

        Assert.False(ai.TryGetLookPoint(1 + AIPerception.HeardForSeconds + 0.5, out _, out _));

        // Someone it has noticed outranks a noise.
        ai.Hear(20, new Vector3(30, 0, 0));
        Watch(ai, 20, 1, Look("p1", 3));
        Assert.True(ai.TryGetLookPoint(21, out _, out why));
        Assert.Equal("noticed", why);
    }

    [Fact]
    public void Hearing_ReachesListenersInRange_WithARoughIdeaOfWhere()
    {
        var shot = new Vector3(0, 0, 0);
        Assert.True(Hearing.CanHear(shot, Hearing.GunshotRadius, new Vector3(100, 0, 0)));
        Assert.False(Hearing.CanHear(shot, Hearing.GunshotRadius, new Vector3(151, 0, 0)));

        var random = new Random(7);
        for (var i = 0; i < 50; i++)
        {
            var guess = Hearing.Rough(shot, new Vector3(100, 0, 0), random);
            Assert.InRange(Vector3.Distance(guess, shot), 0f, 100 * Hearing.Vagueness + 0.001f);
            Assert.Equal(0f, guess.Y);
        }

        Assert.NotEqual(Hearing.Rough(shot, new Vector3(100, 0, 0), new Random(1)), Hearing.Rough(shot, new Vector3(100, 0, 0), new Random(2)));
        Assert.Equal(shot, Hearing.Rough(shot, shot, new Random(3)));    // right beside it, it knows exactly
    }

    [Fact]
    public void WhatOneSquadmateSpots_TheOthersLookTowards()
    {
        var squad = new SquadMemory();
        var spotter = new AIPerception(squad: squad);
        var other = new AIPerception(squad: squad);
        var stranger = new AIPerception();

        var now = Watch(spotter, 0, 0.5, Look("p1", 5));
        Assert.False(other.TryGetLookPoint(now, out _, out _));            // only an alert sighting is shared

        now = Watch(spotter, now, 2, Look("p1", 5));
        Assert.True(spotter.IsAlert);
        Assert.True(other.TryGetLookPoint(now, out var look, out var why));
        Assert.Equal("squad", why);
        Assert.Equal(new Vector3(0, 0, 5), look);
        Assert.StartsWith("squad saw p1", other.Describe(now));
        Assert.Equal(0f, other.AwarenessOf("p1"));                        // it knows where to look, not who's there
        Assert.False(stranger.TryGetLookPoint(now, out _, out _));

        Assert.False(other.TryGetLookPoint(now + AIPerception.HeardForSeconds + 1, out _, out _));
    }

    [Fact]
    public void SquadMemory_KeepsTheLatestSightingOfEach()
    {
        var squad = new SquadMemory();
        squad.Report("p1", new Vector3(1, 0, 0), 5);
        squad.Report("p1", new Vector3(2, 0, 0), 4);                       // older news doesn't overwrite
        squad.Report("p2", new Vector3(3, 0, 0), 6);

        Assert.True(squad.TryGetLatest(7, 10, out var id, out var at, out var time));
        Assert.Equal(("p2", new Vector3(3, 0, 0), 6.0), (id, at, time));
        Assert.True(squad.TryGetLatest(15.5, 10, out id, out at, out _));
        Assert.Equal(("p2", new Vector3(3, 0, 0)), (id, at));
        Assert.False(squad.TryGetLatest(20, 10, out _, out _, out _));
    }

    [Fact]
    public void Visibility_IsNeutralUntilLightFogAndCrouchingExist()
    {
        Assert.Equal(1f, Visibility.Of());
        Assert.Equal(0.25f, Visibility.Of(light: 0.5f, posture: 0.5f), 3);
        Assert.Equal(0f, Visibility.Of(clarity: -1f));
        Assert.Equal(1f, Visibility.Of(light: 3f));
    }
}
