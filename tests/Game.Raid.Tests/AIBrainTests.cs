using System.Numerics;

namespace RaidZero.Raid.Tests;

/// <summary>The AI's brain, driving a fake body through a fake world with real perception.</summary>
public class AIBrainTests
{
    // ---- A fake body, a fake world, and a clock ----

    private sealed class FakeBody : IAIBody
    {
        public Vector3 Position { get; set; }
        public Vector3 Forward { get; set; } = Vector3.UnitZ;
        public float HealthFraction { get; set; } = 1f;
        public int LoadedRounds { get; set; } = 30;
        public int Capacity { get; set; } = 31;
        public int SpareMagazines { get; set; } = 2;
        public bool CanReload => SpareMagazines > 0;
        public bool IsReloading => ReloadDoneAt > Clock;
        public bool HasArrived => !Goal.HasValue;
        public bool IsStuck { get; set; }
        public bool Trigger { get; private set; }
        public bool BlockFire { get; set; }
        public Vector3? Goal { get; private set; }
        public bool Running { get; private set; }
        public Vector3 Aim { get; private set; }
        public int ShotsFired { get; private set; }
        public int MoveOrders { get; private set; }
        public double Clock;
        public double ReloadDoneAt = double.MinValue;
        public double FirstShotAt = double.NaN;

        public void MoveTo(Vector3 point, bool run) { Goal = point; Running = run; MoveOrders++; }
        public void Stop() => Goal = null;
        public void LookAt(Vector3 point)
        {
            Aim = point;
            var flat = new Vector3(point.X - Position.X, 0, point.Z - Position.Z);
            if (flat.LengthSquared() > 0.0001f)
            {
                Forward = Vector3.Normalize(flat);
            }
        }

        public void SetTrigger(bool held) => Trigger = held;
        public void Reload()
        {
            if (CanReload && !IsReloading)
            {
                ReloadDoneAt = Clock + 2;
                SpareMagazines--;
            }
        }

        public bool HasLineOfFire(Vector3 point) => !BlockFire;

        public void Step(double dt)
        {
            Clock += dt;
            if (ReloadDoneAt != double.MinValue && Clock >= ReloadDoneAt)
            {
                LoadedRounds = Capacity - 1;
                ReloadDoneAt = double.MinValue;
            }

            if (Trigger && LoadedRounds > 0 && !IsReloading)
            {
                LoadedRounds--;
                ShotsFired++;
                if (double.IsNaN(FirstShotAt))
                {
                    FirstShotAt = Clock;
                }
            }

            if (Goal.HasValue && !IsStuck)
            {
                var to = Goal.Value - Position;
                to.Y = 0;
                var step = (float)((Running ? 4.5 : 2.0) * dt);
                Position = to.Length() <= step ? new Vector3(Goal.Value.X, Position.Y, Goal.Value.Z) : Position + Vector3.Normalize(to) * step;
                if (to.Length() <= step)
                {
                    Goal = null;
                }
            }
        }
    }

    /// <summary>Walls are boxes; cover is a list.</summary>
    private sealed class FakeWorld : IAIWorld
    {
        public readonly List<(Vector3 Min, Vector3 Max)> Walls = new();
        public readonly List<CoverSpot> Cover = new();

        public IEnumerable<CoverSpot> CoverNear(Vector3 point, float radius) => Cover.Where(c => Vector3.Distance(c.Position, point) <= radius);

        public bool IsBlocked(Vector3 from, Vector3 to) => Walls.Any(w => SegmentHitsBox(from, to, w.Min, w.Max));

        private static bool SegmentHitsBox(Vector3 a, Vector3 b, Vector3 min, Vector3 max)
        {
            double t0 = 0, t1 = 1;
            for (int axis = 0; axis < 3; axis++)
            {
                double p = axis == 0 ? a.X : axis == 1 ? a.Y : a.Z;
                double d = (axis == 0 ? b.X : axis == 1 ? b.Y : b.Z) - p;
                double lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
                double hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
                if (Math.Abs(d) < 1e-9)
                {
                    if (p < lo || p > hi) return false;
                    continue;
                }

                double ta = (lo - p) / d, tb = (hi - p) / d;
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = Math.Max(t0, ta);
                t1 = Math.Min(t1, tb);
                if (t0 > t1) return false;
            }

            return true;
        }
    }

    /// <summary>A target (a player) standing or walking somewhere.</summary>
    private sealed class Someone
    {
        public string Id = "p1";
        public Vector3 Position;
        public Vector3 Velocity;
        public bool Present = true;
    }

    /// <summary>One AI: its fake body, real perception and a brain, with the world and the people in it.</summary>
    private sealed class Rig
    {
        public readonly FakeBody Body = new();
        public readonly FakeWorld World;
        public readonly AIPerception Perception;
        public readonly AIBrain Brain;
        public readonly List<Someone> People = new();

        public Rig(string id, Vector3 at, AIBrainSettings? settings = null, FakeWorld? world = null, SquadMemory? squad = null,
            IReadOnlyList<Vector3>? route = null, bool loop = true)
        {
            Body.Position = at;
            World = world ?? new FakeWorld();
            Perception = new AIPerception(new SightSettings(), squad);
            Brain = new AIBrain(id, Body, Perception, settings ?? AIBrainSettings.Contractors(), World, route, loop, seed: 1);
        }

        public double Now => Body.Clock;

        public void Step(double dt = 0.1)
        {
            foreach (var person in People)
            {
                person.Position += person.Velocity * (float)dt;
            }

            Body.Step(dt);
            var eye = Body.Position + Vector3.UnitY * 1.6f;
            var samples = new List<SightSample>();
            foreach (var person in People.Where(p => p.Present))
            {
                var chest = person.Position + Vector3.UnitY * 1.15f;
                var towards = new Vector3(chest.X - eye.X, 0, chest.Z - eye.Z);
                var angle = towards.LengthSquared() < 0.0001f ? 0f : CoverPickerAngle(Body.Forward, towards);
                samples.Add(new SightSample
                {
                    TargetId = person.Id,
                    Position = person.Position,
                    Distance = Vector3.Distance(eye, chest),
                    AngleOffCentre = angle,
                    InLineOfSight = !World.IsBlocked(eye, chest),
                    Speed = person.Velocity.Length(),
                    Visibility = 1f,
                });
            }

            Perception.See(Now, (float)dt, samples);
            Brain.Tick(Now);
        }

        public void Run(double seconds, Func<bool>? until = null)
        {
            for (var i = 0; i < (int)Math.Round(seconds * 10); i++)
            {
                Step();
                if (until != null && until())
                {
                    return;
                }
            }
        }

        private static float CoverPickerAngle(Vector3 a, Vector3 b)
        {
            var dot = Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b));
            return (float)(Math.Acos(Math.Clamp(dot, -1f, 1f)) * 180 / Math.PI);
        }
    }

    private static Vector3 V(float x, float z) => new(x, 0, z);

    // ---- Patrol ----

    [Fact]
    public void Patrol_WalksItsRouteInOrder_StoppingAtEachPoint()
    {
        var route = new[] { V(0, 10), V(10, 10), V(10, 0) };
        var rig = new Rig("ai-1", V(0, 0), AIBrainSettings.Contractors(), route: route);
        var visited = new List<int>();
        for (var i = 0; i < 600; i++)
        {
            rig.Step();
            var at = Array.FindIndex(route, p => Vector3.Distance(p, rig.Body.Position) < 1.3f);
            if (at >= 0 && (visited.Count == 0 || visited[^1] != at))
            {
                visited.Add(at);
            }
        }

        Assert.Equal(AIState.Patrol, rig.Brain.State);
        Assert.Equal(new[] { 0, 1, 2, 0, 1 }, visited.Take(5));
        Assert.False(rig.Body.Running);
    }

    [Fact]
    public void Patrol_ThereAndBack_WhenTheRouteDoesntLoop()
    {
        var route = new[] { V(0, 5), V(0, 10), V(0, 15) };
        var rig = new Rig("ai-1", V(0, 5), AIBrainSettings.Contractors(), route: route, loop: false);
        var visited = new List<int>();
        for (var i = 0; i < 900; i++)
        {
            rig.Step();
            var at = Array.FindIndex(route, p => Vector3.Distance(p, rig.Body.Position) < 1.3f);
            if (at >= 0 && (visited.Count == 0 || visited[^1] != at))
            {
                visited.Add(at);
            }
        }

        Assert.Equal(new[] { 0, 1, 2, 1, 0, 1 }, visited.Take(6));
    }

    [Fact]
    public void Looters_WanderBetweenTheirPoints_NeverPickingTheOneTheyreAt()
    {
        var route = new[] { V(0, 0), V(6, 0), V(0, 6), V(6, 6) };
        var settings = AIBrainSettings.Looters();
        settings.PatrolPauseSeconds = 1;
        var rig = new Rig("ai-1", V(0, 0), settings, route: route);
        var visited = new List<int>();
        for (var i = 0; i < 1500; i++)
        {
            rig.Step();
            var at = Array.FindIndex(route, p => Vector3.Distance(p, rig.Body.Position) < 1.3f);
            if (at >= 0 && (visited.Count == 0 || visited[^1] != at))
            {
                visited.Add(at);
            }
        }

        Assert.True(visited.Count >= 8);
        Assert.Equal(4, visited.Distinct().Count());
        Assert.NotEqual(new[] { 0, 1, 2, 3, 0, 1, 2, 3 }, visited.Take(8));
    }

    [Fact]
    public void WithNoRoute_ItGuardsWhereItSpawned_AndLooksAbout()
    {
        var rig = new Rig("ai-1", V(3, 3));
        var facings = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            rig.Step();
            facings.Add(rig.Body.Forward.ToString("0.0", null));
        }

        Assert.Equal(V(3, 3), rig.Body.Position);
        Assert.Equal("Patrol: guarding", rig.Brain.Describe());
        Assert.True(facings.Count > 2);
    }

    // ---- Investigate ----

    [Fact]
    public void AShot_SendsItToLook_ThenBackToItsPost()
    {
        var rig = new Rig("ai-1", V(0, 0));
        rig.Run(1);
        rig.Perception.Hear(rig.Now, V(0, -20)); // behind it
        rig.Step();
        Assert.Equal(AIState.Investigate, rig.Brain.State);
        Assert.False(rig.Body.Running);                         // carefully

        rig.Run(15, () => Vector3.Distance(rig.Body.Position, V(0, -20)) < 1.3f);
        Assert.InRange(Vector3.Distance(rig.Body.Position, V(0, -20)), 0, 1.3);
        rig.Run(AIBrain.LookAroundSeconds - 1);
        Assert.Equal(AIState.Investigate, rig.Brain.State);   // looking around
        rig.Run(2);
        Assert.Equal(AIState.Patrol, rig.Brain.State);

        rig.Run(15);
        Assert.InRange(Vector3.Distance(rig.Body.Position, V(0, 0)), 0, AIBrain.ArriveDistance);   // home again
        rig.Run(1);
        Assert.Equal(AIState.Patrol, rig.Brain.State);        // the same shot doesn't send it twice
    }

    [Fact]
    public void Glimpsing_Someone_ItWalksTowardsThem_UntilItsSure()
    {
        var rig = new Rig("ai-1", V(0, 0), AIBrainSettings.Looters());
        rig.People.Add(new Someone { Position = V(3, 60), Velocity = V(-0.5f, 0) });  // far, ambling: awareness creeps up
        rig.Run(10, () => rig.Brain.State != AIState.Patrol);
        Assert.Equal(AIState.Investigate, rig.Brain.State);

        var start = rig.Body.Position;
        rig.Run(1);
        Assert.True(rig.Body.Position.Z > start.Z);            // walking over to look
        rig.Run(20, () => rig.Brain.State == AIState.Attack);
        Assert.Equal(AIState.Attack, rig.Brain.State);
        Assert.Equal("p1", rig.Brain.Target);
    }

    // ---- Attack ----

    [Fact]
    public void Alert_ItShoots_OnlyAfterItsReactionTime_AndAimsAtTheChest()
    {
        var settings = AIBrainSettings.Contractors();
        var rig = new Rig("ai-1", V(0, 0), settings);
        rig.People.Add(new Someone { Position = V(0, 12) });
        rig.Run(5, () => rig.Brain.State == AIState.Attack);
        var alertAt = rig.Now;
        rig.Run(3);

        Assert.Equal(AIState.Attack, rig.Brain.State);
        Assert.True(rig.Body.ShotsFired > 0);
        Assert.True(rig.Body.FirstShotAt - alertAt >= settings.ReactionSeconds - 0.11);
        Assert.InRange(rig.Body.Aim.Y, 0.0, 3.0);
        Assert.InRange(Vector3.Distance(rig.Body.Aim, V(0, 12) + Vector3.UnitY * 1.15f), 0, 2);
    }

    [Fact]
    public void ItNeverShoots_WithoutALineOfFire()
    {
        var rig = new Rig("ai-1", V(0, 0));
        rig.Body.BlockFire = true;                                  // sees over a wall its gun can't
        rig.People.Add(new Someone { Position = V(0, 12) });
        rig.Run(6);
        Assert.Equal(AIState.Attack, rig.Brain.State);
        Assert.Equal(0, rig.Body.ShotsFired);
        Assert.False(rig.Brain.Firing);
    }

    [Fact]
    public void AnEmptyGun_IsReloaded_FromASpareMagazine()
    {
        var rig = new Rig("ai-1", V(0, 0));
        rig.People.Add(new Someone { Position = V(0, 12) });
        rig.Run(5, () => rig.Brain.State == AIState.Attack);
        rig.Run(15, () => rig.Body.LoadedRounds == 0);
        rig.Run(1);
        Assert.Equal(AttackAction.Reload, rig.Brain.Action);
        rig.Run(3);
        Assert.Equal(1, rig.Body.SpareMagazines);
        Assert.True(rig.Body.LoadedRounds > 0);
    }

    [Fact]
    public void OutOfSight_ItSearches_WhereItLastSawThem_ThenTheCoverNearby_ThenGivesUp()
    {
        var world = new FakeWorld();
        world.Walls.Add((new Vector3(-20, 0, 14), new Vector3(20, 3, 15)));   // a wall across, at z 14
        world.Cover.Add(new CoverSpot(V(4, 18), -Vector3.UnitZ, low: false));
        world.Cover.Add(new CoverSpot(V(-5, 20), -Vector3.UnitZ, low: false));
        var rig = new Rig("ai-1", V(0, 0), AIBrainSettings.Contractors(), world);
        var player = new Someone { Position = V(0, 12) };
        rig.People.Add(player);
        rig.Run(5, () => rig.Brain.State == AIState.Attack);

        player.Present = false;                                                 // over the wall and away
        rig.Run(AIBrain.LoseTargetSeconds + 1);
        Assert.Equal(AIState.Search, rig.Brain.State);
        Assert.Equal("Search: 1 of 3", rig.Brain.Describe());

        rig.Run(AIBrain.SearchSeconds + 1, () => rig.Brain.State != AIState.Search);
        Assert.Equal(AIState.Patrol, rig.Brain.State);
        rig.Run(30);
        Assert.InRange(Vector3.Distance(rig.Body.Position, V(0, 0)), 0, AIBrain.ArriveDistance);   // and goes home
    }

    [Fact]
    public void HurtBelowItsCourage_ItRetreatsToCoverAway_Reloads_AndGoesBackToItsPost()
    {
        var world = new FakeWorld();
        world.Walls.Add((new Vector3(-2, 0, -12), new Vector3(2, 3, -11)));
        world.Cover.Add(new CoverSpot(V(0, -13), Vector3.UnitZ, low: false));  // behind that wall, facing the fight
        var rig = new Rig("ai-1", V(0, 0), AIBrainSettings.Looters(), world);
        var player = new Someone { Position = V(0, 12) };
        rig.People.Add(player);
        rig.Run(6, () => rig.Brain.State == AIState.Attack);
        rig.Run(2);
        rig.Body.LoadedRounds = 10;
        rig.Body.HealthFraction = 0.4f;                       // under a looter's 0.5
        rig.Step();

        Assert.Equal(AIState.Retreat, rig.Brain.State);
        Assert.True(rig.Body.Running);
        Assert.Equal(V(0, -13), rig.Brain.Cover!.Value.Position);
        rig.Run(10, () => Vector3.Distance(rig.Body.Position, V(0, -13)) < 1.3f);
        player.Present = false;                               // and you leave it be
        rig.Run(1);
        Assert.True(rig.Body.IsReloading || rig.Body.LoadedRounds == 30);
        rig.Run(AIBrain.RetreatWaitSeconds + 1);
        Assert.Equal(AIState.Patrol, rig.Brain.State);        // still hurt: back to its post, not hunting
    }

    [Fact]
    public void AContractor_HurtAsMuch_KeepsFighting()
    {
        var rig = new Rig("ai-1", V(0, 0), AIBrainSettings.Contractors());
        rig.People.Add(new Someone { Position = V(0, 12) });
        rig.Run(6, () => rig.Brain.State == AIState.Attack);
        rig.Body.HealthFraction = 0.4f;                       // over a contractor's 0.25
        rig.Run(3);
        Assert.Equal(AIState.Attack, rig.Brain.State);
    }

    [Fact]
    public void OutOfAmmo_ItRetreats_AwayFromTheThreat()
    {
        var rig = new Rig("ai-1", V(0, 0), AIBrainSettings.Contractors());
        rig.People.Add(new Someone { Position = V(0, 15) });
        rig.Run(6, () => rig.Brain.State == AIState.Attack);
        rig.Body.LoadedRounds = 0;
        rig.Body.SpareMagazines = 0;
        rig.Step();
        Assert.Equal(AIState.Retreat, rig.Brain.State);
        Assert.True(rig.Body.Running);
        Assert.True(rig.Body.Goal!.Value.Z < -15);             // 20 m away, with no cover to go to
        Assert.False(rig.Brain.Firing);
    }

    [Fact]
    public void Retreating_ButCornered_ItFights()
    {
        var rig = new Rig("ai-1", V(0, 0), AIBrainSettings.Looters());
        var player = new Someone { Position = V(0, 15) };
        rig.People.Add(player);
        rig.Run(6, () => rig.Brain.State == AIState.Attack);
        rig.Body.HealthFraction = 0.3f;
        rig.Step();
        Assert.Equal(AIState.Retreat, rig.Brain.State);

        player.Position = rig.Body.Position + V(0, 4);         // right on top of it
        rig.Step();
        Assert.Equal(AIState.Attack, rig.Brain.State);
        rig.Run(2);
        Assert.Equal(AIState.Attack, rig.Brain.State);         // and doesn't flee again straight away
    }

    [Fact]
    public void DeadBrains_DoNothing_AndLeaveTheSquad()
    {
        var squad = new SquadMemory();
        var rig = new Rig("ai-1", V(0, 0), squad: squad);
        rig.Run(1);
        Assert.Equal(1, squad.Count);
        rig.Body.HealthFraction = 0f;
        rig.Step();
        Assert.Equal(AIState.Dead, rig.Brain.State);
        Assert.Equal(0, squad.Count);
        rig.Perception.Hear(rig.Now, V(5, 5));
        rig.Run(1);
        Assert.Equal(AIState.Dead, rig.Brain.State);
    }

    // ---- The squad ----

    [Fact]
    public void OneSpotsYou_TheOtherComesRunning()
    {
        var squad = new SquadMemory();
        var spotter = new Rig("ai-1", V(0, 0), AIBrainSettings.Looters(), squad: squad);
        var partner = new Rig("ai-2", V(30, -30), AIBrainSettings.Looters(), squad: squad);
        partner.Body.Forward = -Vector3.UnitZ;                 // looking the other way
        spotter.People.Add(new Someone { Position = V(0, 10) });
        for (var i = 0; i < 60; i++)
        {
            spotter.Step();
            partner.Step();
        }

        Assert.Equal(AIState.Attack, spotter.Brain.State);
        Assert.Equal(AIState.Investigate, partner.Brain.State);
        Assert.True(partner.Body.Running);
        Assert.InRange(Vector3.Distance(partner.Body.Goal!.Value, V(0, 10)), 0, 0.01);
    }

    [Fact]
    public void BeingShot_SendsItRunningTowardsTheShooter_AndTellsTheSquad()
    {
        var squad = new SquadMemory();
        var rig = new Rig("ai-1", V(0, 0), squad: squad);
        rig.Run(1);
        rig.Brain.WasHit(rig.Now, "p1", V(-25, 0));
        rig.Step();
        Assert.Equal(AIState.Investigate, rig.Brain.State);
        Assert.True(rig.Body.Running);
        Assert.True(squad.TryGetLatest(rig.Now, 10, out var who, out var where, out _));
        Assert.Equal(("p1", V(-25, 0)), (who, where));
    }

    [Fact]
    public void AContractor_FlanksWhenASquadmateIsAlreadyOnTheTarget()
    {
        var squad = new SquadMemory();
        squad.Join("ai-mate");
        squad.SetActivity("ai-mate", AIState.Attack, "p1");
        var settings = AIBrainSettings.Contractors();
        settings.PushWeight = 10;                              // make it push
        var rig = new Rig("ai-1", V(0, 0), settings, squad: squad);
        rig.People.Add(new Someone { Position = V(0, 40) });   // beyond its range
        rig.Run(10, () => rig.Brain.Action == AttackAction.Push);
        Assert.Equal(AttackAction.Push, rig.Brain.Action);

        var goal = rig.Brain.MoveTarget!.Value;
        Assert.True(Math.Abs(goal.X) > 10);                     // round the side, not straight up the middle
        Assert.InRange(Vector3.Distance(goal, V(0, 40)), 14, 16);
    }

    // ---- The pieces ----

    [Fact]
    public void Scoring_LootersPushWithTheNumbers_ContractorsHoldCover_AndAnEmptyGunReloads()
    {
        var looters = AIBrainSettings.Looters();
        var contractors = AIBrainSettings.Contractors();
        var far = new AttackSituation { Distance = 30, InView = true, HasLineOfFire = true, LoadedRounds = 20, Capacity = 31, CanReload = true, HealthFraction = 1, Advantage = 2 };
        Assert.Equal(AttackAction.Push, AttackScoring.Choose(far, looters));
        Assert.Equal(AttackAction.Hold, AttackScoring.Choose(far with { Advantage = 0.5f, Distance = 12 }, looters));

        var covered = far with { Distance = 20, InCover = true, CoverAvailable = false, Advantage = 1 };
        Assert.Equal(AttackAction.Hold, AttackScoring.Choose(covered, contractors));
        var inTheOpen = covered with { InCover = false, CoverAvailable = true };
        Assert.Equal(AttackAction.Reposition, AttackScoring.Choose(inTheOpen, contractors));

        Assert.Equal(AttackAction.Reload, AttackScoring.Choose(far with { LoadedRounds = 0 }, contractors));
        Assert.NotEqual(AttackAction.Reload, AttackScoring.Choose(far with { LoadedRounds = 0, CanReload = false }, contractors));
        Assert.Equal(AttackAction.Reload, AttackScoring.Choose(covered with { LoadedRounds = 4, InView = false }, contractors));
    }

    [Fact]
    public void Cover_MustHideItFromTheThreat_BeNearerToIt_AndNotBeTaken()
    {
        var world = new FakeWorld();
        world.Walls.Add((new Vector3(-10, 0, 9), new Vector3(10, 2, 10)));    // a long wall at z 9..10, 2 m high
        var good = new CoverSpot(V(2, 8), Vector3.UnitZ, low: false);
        var facingAway = new CoverSpot(V(-2, 8), -Vector3.UnitZ, low: false);
        var nearerThem = new CoverSpot(V(0, 18), Vector3.UnitZ, low: false);
        var open = new CoverSpot(V(15, 5), Vector3.UnitZ, low: false);       // past the wall's end: nothing in the way
        world.Cover.AddRange(new[] { facingAway, nearerThem, open, good });
        var self = V(0, 0);
        var threat = V(0, 25);

        Assert.True(CoverPicker.TryPick(world, self, threat, 20, away: false, null, "ai-1", out var picked));
        Assert.Equal(good.Position, picked.Position);
        Assert.False(CoverPicker.Protects(facingAway, threat, world));
        Assert.False(CoverPicker.Protects(open, threat, world));

        var squad = new SquadMemory();
        squad.ClaimCover("ai-2", good.Position);
        Assert.False(CoverPicker.TryPick(world, self, threat, 20, away: false, squad, "ai-1", out _));
        Assert.True(CoverPicker.TryPick(world, self, threat, 20, away: false, squad, "ai-2", out _));   // its own

        // Retreating: only cover further from the threat than it is now.
        Assert.False(CoverPicker.TryPick(world, V(0, 6), threat, 20, away: true, null, "ai-1", out _));
    }

    [Fact]
    public void Aim_IsLooserFarAway_TightensAsItTracks_AndSlipsOnAMovingTarget()
    {
        var s = AIBrainSettings.Contractors();
        Assert.True(AIAim.ErrorRadius(s, 40, 0, 0) > AIAim.ErrorRadius(s, 10, 0, 0));
        Assert.True(AIAim.ErrorRadius(s, 20, s.AimSettleSeconds, 0) < AIAim.ErrorRadius(s, 20, 0, 0) * 0.4f);
        Assert.True(AIAim.ErrorRadius(s, 20, 0, 6) > AIAim.ErrorRadius(s, 20, 0, 0) * 1.9f);
        Assert.Equal(s.AimErrorAt20m, AIAim.ErrorRadius(s, 20, 0, 0), 3);

        var random = new Random(3);
        var target = new Vector3(5, 1, 5);
        for (var i = 0; i < 200; i++)
        {
            Assert.InRange(Vector3.Distance(AIAim.Around(target, 0.8f, random), target), 0, 0.8001f);
        }
    }

    [Fact]
    public void Squad_CountsWhoIsDoingWhat_AndKnownHostiles()
    {
        var squad = new SquadMemory();
        squad.Join("a");
        squad.Join("b");
        squad.Join("c");
        squad.SetActivity("a", AIState.Attack, "p1");
        squad.SetActivity("b", AIState.Attack, "p2");
        squad.SetActivity("ghost", AIState.Attack, "p1");      // not a member: ignored
        Assert.Equal(3, squad.Count);
        Assert.Equal(2, squad.CountIn(AIState.Attack));
        Assert.Equal(1, squad.CountIn(AIState.Attack, "p1"));
        Assert.Equal(1, squad.CountIn(AIState.Attack, null, "a"));

        squad.ClaimCover("a", V(1, 1));
        Assert.True(squad.IsCoverTaken(V(1.5f, 1), "b"));
        squad.Leave("a");
        Assert.False(squad.IsCoverTaken(V(1, 1), "b"));
        Assert.Equal(2, squad.Count);

        squad.Report("p1", V(0, 0), 1);
        squad.Report("p2", V(0, 0), 20);
        Assert.Equal(2, squad.KnownHostiles(25, 30));
        Assert.Equal(1, squad.KnownHostiles(40, 30));
    }
}
