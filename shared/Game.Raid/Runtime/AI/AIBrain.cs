using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace RaidZero.Raid
{
    /// <summary>What an AI is doing (see <see cref="AIBrain"/>).</summary>
    public enum AIState : byte
    {
        Patrol,
        Investigate,
        Attack,
        Search,
        Retreat,
        Dead,
    }

    /// <summary>What an AI in <see cref="AIState.Attack"/> is doing about its target, chosen about once a second (<see cref="AttackScoring"/>).</summary>
    public enum AttackAction : byte
    {
        None,

        /// <summary>Close in to its preferred range; a contractor whose squadmate is already fighting goes round the side.</summary>
        Push,

        /// <summary>Stay put (in cover, if it's in cover) and shoot.</summary>
        Hold,

        /// <summary>Move to cover that hides it from the target.</summary>
        Reposition,

        Reload,
    }

    /// <summary>
    /// What the brain drives: the AI's body in the world. John's AIBody implements it on the character motor and
    /// the gun (moving along a NavMesh path, aiming and pulling the trigger as a player would); the target dummy
    /// has a stand-in until then; the tests have a fake. Positions are at the feet.
    /// </summary>
    public interface IAIBody
    {
        Vector3 Position { get; }

        /// <summary>Which way it faces (flat).</summary>
        Vector3 Forward { get; }

        /// <summary>Its health, 0 to 1: at 0 it's dead.</summary>
        float HealthFraction { get; }

        /// <summary>Rounds it can fire before reloading, from the raid server (RaidServer.LoadedRounds).</summary>
        int LoadedRounds { get; }

        /// <summary>What a full load holds: its magazine's capacity plus the chamber.</summary>
        int Capacity { get; }

        /// <summary>Whether it has a fuller magazine to reload with.</summary>
        bool CanReload { get; }

        bool IsReloading { get; }

        /// <summary>Whether it's got where <see cref="MoveTo"/> last sent it (or has nowhere to go).</summary>
        bool HasArrived { get; }

        /// <summary>Whether it gave up getting there: no path, or no progress after asking for a new one three times.</summary>
        bool IsStuck { get; }

        /// <summary>Walk (or run) to there, by a path. Asking again for the same place changes nothing.</summary>
        void MoveTo(Vector3 point, bool run);

        void Stop();

        /// <summary>Turn to look, and aim, at this point.</summary>
        void LookAt(Vector3 point);

        /// <summary>Hold (or let go of) the trigger: the gun fires as its fire mode allows, from its muzzle along the aim.</summary>
        void SetTrigger(bool held);

        void Reload();

        /// <summary>Whether a shot from its muzzle would reach that point: its eyes may see over a wall its gun can't.</summary>
        bool HasLineOfFire(Vector3 point);
    }

    /// <summary>What the brain asks of the world around it: the map's cover, and whether a line of sight is blocked.</summary>
    public interface IAIWorld
    {
        /// <summary>The cover markers within <paramref name="radius"/> of a point.</summary>
        IEnumerable<CoverSpot> CoverNear(Vector3 point, float radius);

        /// <summary>Whether something solid is between the two points.</summary>
        bool IsBlocked(Vector3 from, Vector3 to);
    }

    /// <summary>A place to take cover (a CoverPoint marker in a map): where, the side it protects from, and how high.</summary>
    public struct CoverSpot
    {
        public Vector3 Position;

        /// <summary>Towards the side it protects from, flat: a wall's cover faces the wall.</summary>
        public Vector3 Facing;

        /// <summary>Low cover hides someone crouching; high cover, someone standing.</summary>
        public bool Low;

        public CoverSpot(Vector3 position, Vector3 facing, bool low)
        {
            Position = position;
            Facing = facing;
            Low = low;
        }
    }

    /// <summary>How a kind of AI fights: from its profile, or its faction's defaults (<see cref="For"/>).</summary>
    public sealed class AIBrainSettings
    {
        /// <summary>Between becoming alert and its first shot, in seconds.</summary>
        public float ReactionSeconds { get; set; } = 0.6f;

        /// <summary>How far off its aim is at 20 m, in metres, before it's settled on a target (<see cref="AIAim"/>).</summary>
        public float AimErrorAt20m { get; set; } = 1.2f;

        /// <summary>How long tracking one target takes its aim from wide to tight.</summary>
        public float AimSettleSeconds { get; set; } = 2f;

        public float PushWeight { get; set; } = 1f;
        public float HoldWeight { get; set; } = 1f;
        public float RepositionWeight { get; set; } = 1f;
        public float ReloadWeight { get; set; } = 1f;

        /// <summary>How much having the numbers makes it push: 0 not at all, 1 a lot.</summary>
        public float NumbersMatter { get; set; } = 0.5f;

        /// <summary>Whether it goes round the side when a squadmate is already fighting the same target.</summary>
        public bool Flanks { get; set; }

        /// <summary>Below this much health (0 to 1) it retreats.</summary>
        public float Courage { get; set; } = 0.35f;

        /// <summary>The distance it likes to fight at, in metres.</summary>
        public float PreferredRange { get; set; } = 20f;

        /// <summary>Whether it visits its route's points in any order (looters wandering between loot spots) or in order (a patrol).</summary>
        public bool Wanders { get; set; }

        /// <summary>How long it stops at each point of its route.</summary>
        public float PatrolPauseSeconds { get; set; } = 4f;

        /// <summary>Opportunistic: push when they have the numbers, break off when hurt, slow and loose with their aim.</summary>
        public static AIBrainSettings Looters() => new AIBrainSettings
        {
            ReactionSeconds = 0.8f,
            AimErrorAt20m = 1.5f,
            AimSettleSeconds = 2.5f,
            PushWeight = 1.3f,
            HoldWeight = 0.8f,
            RepositionWeight = 0.6f,
            ReloadWeight = 1f,
            NumbersMatter = 1f,
            Flanks = false,
            Courage = 0.5f,
            PreferredRange = 15f,
            Wanders = true,
            PatrolPauseSeconds = 8f,
        };

        /// <summary>Disciplined: hold cover, flank, stay in the fight; quicker and tighter with their aim.</summary>
        public static AIBrainSettings Contractors() => new AIBrainSettings
        {
            ReactionSeconds = 0.5f,
            AimErrorAt20m = 0.9f,
            AimSettleSeconds = 1.5f,
            PushWeight = 0.7f,
            HoldWeight = 1.3f,
            RepositionWeight = 1.2f,
            ReloadWeight = 1f,
            NumbersMatter = 0.3f,
            Flanks = true,
            Courage = 0.25f,
            PreferredRange = 25f,
            Wanders = false,
            PatrolPauseSeconds = 4f,
        };

        public static AIBrainSettings For(RaidZero.Shared.Raid.Faction faction) =>
            faction == RaidZero.Shared.Raid.Faction.Contractors ? Contractors() : Looters();
    }

    /// <summary>How far off an AI's aim is: the aim point the brain gives the body. The gun's own spread and recoil come on top.</summary>
    public static class AIAim
    {
        /// <summary>
        /// The radius of its aiming error, in metres at the target: wider with distance, tightening to a third the
        /// longer it tracks the same target, wider for a fast-moving one.
        /// </summary>
        public static float ErrorRadius(AIBrainSettings settings, float distance, double secondsTracking, float targetSpeed)
        {
            var settle = settings.AimSettleSeconds <= 0f ? 1f : (float)Math.Min(1.0, Math.Max(0.0, secondsTracking / settings.AimSettleSeconds));
            var tracking = 1f + (0.35f - 1f) * settle;
            return settings.AimErrorAt20m * (Math.Max(2f, distance) / 20f) * tracking * (1f + Math.Max(0f, targetSpeed) / 6f);
        }

        /// <summary>A point within <paramref name="radius"/> of the target.</summary>
        public static Vector3 Around(Vector3 target, float radius, Random random)
        {
            // Uniform in a ball: a direction, and a distance that favours the outside as a ball's volume does.
            var z = (float)(random.NextDouble() * 2 - 1);
            var angle = random.NextDouble() * Math.PI * 2;
            var ring = (float)Math.Sqrt(1 - z * z);
            var direction = new Vector3(ring * (float)Math.Cos(angle), z, ring * (float)Math.Sin(angle));
            return target + direction * (radius * (float)Math.Pow(random.NextDouble(), 1.0 / 3.0));
        }
    }

    /// <summary>What an AI in a fight knows when it chooses what to do (<see cref="AttackScoring.Choose"/>).</summary>
    public struct AttackSituation
    {
        public float Distance;
        public bool InView;
        public bool HasLineOfFire;
        public bool InCover;
        public bool CoverAvailable;
        public int LoadedRounds;
        public int Capacity;
        public bool CanReload;
        public float HealthFraction;

        /// <summary>Its side's fighters against the hostiles they know of: 2 is two of them to one.</summary>
        public float Advantage;

        /// <summary>What it's doing now: a little sticky, so it doesn't flip every second.</summary>
        public AttackAction Current;
    }

    /// <summary>The choice inside Attack: push, hold, reposition or reload, each scored and weighted by the profile.</summary>
    public static class AttackScoring
    {
        private const float Stickiness = 0.15f;

        public static float Score(AttackAction action, AttackSituation s, AIBrainSettings settings)
        {
            float score;
            switch (action)
            {
                case AttackAction.Push:
                    score = 0.5f * settings.PushWeight
                        * (s.Distance > settings.PreferredRange ? 1.5f : 0.4f)
                        * (float)Math.Pow(Math.Max(0.25f, Math.Min(4f, s.Advantage)), settings.NumbersMatter)
                        * Math.Max(0.1f, s.HealthFraction)
                        * (s.LoadedRounds > 0 ? 1f : 0.2f);
                    break;
                case AttackAction.Hold:
                    score = 0.6f * settings.HoldWeight
                        * (s.InCover ? 1.5f : 0.7f)
                        * (s.InView && s.HasLineOfFire ? 1f : 0.4f)
                        * (s.Distance <= settings.PreferredRange * 1.5f ? 1f : 0.6f)
                        * (s.LoadedRounds > 0 ? 1f : 0.1f);
                    break;
                case AttackAction.Reposition:
                    score = !s.CoverAvailable ? 0f : 0.5f * settings.RepositionWeight * (s.InCover ? 0.3f : 1.2f);
                    break;
                case AttackAction.Reload:
                    if (!s.CanReload || s.Capacity <= 0 || s.LoadedRounds >= s.Capacity)
                    {
                        score = 0f;
                    }
                    else if (s.LoadedRounds == 0)
                    {
                        score = 10f;
                    }
                    else
                    {
                        var spent = 1f - (float)s.LoadedRounds / s.Capacity;
                        score = settings.ReloadWeight * spent * spent * (s.InView ? 0.5f : 1.5f);
                    }

                    break;
                default:
                    return 0f;
            }

            return action == s.Current ? score + Stickiness : score;
        }

        public static AttackAction Choose(AttackSituation situation, AIBrainSettings settings)
        {
            var best = AttackAction.Hold;
            var bestScore = float.MinValue;
            foreach (var action in new[] { AttackAction.Hold, AttackAction.Push, AttackAction.Reposition, AttackAction.Reload })
            {
                var score = Score(action, situation, settings);
                if (score > bestScore)
                {
                    best = action;
                    bestScore = score;
                }
            }

            return best;
        }
    }

    /// <summary>Which cover to take: it must hide the AI from the threat, and be nearer the AI than the threat is.</summary>
    public static class CoverPicker
    {
        public const float SearchRadius = 25f;
        public const float MaxFacingAngle = 70f;
        public const float EyeHeight = 1.55f;

        /// <summary>Whether the spot hides someone in it from a threat standing there: it faces the threat, and the line from the threat's eyes is blocked.</summary>
        public static bool Protects(CoverSpot spot, Vector3 threat, IAIWorld world)
        {
            var toThreat = Flat(threat - spot.Position);
            if (toThreat.LengthSquared() < 1f)
            {
                return false;
            }

            var facing = Flat(spot.Facing);
            if (facing.LengthSquared() < 0.0001f || AngleBetween(facing, toThreat) > MaxFacingAngle)
            {
                return false;
            }

            var head = spot.Position + Vector3.UnitY * (spot.Low ? 0.9f : 1.4f);
            return world == null || world.IsBlocked(threat + Vector3.UnitY * EyeHeight, head);
        }

        /// <summary>
        /// The best cover near the AI against the threat: closest to it, and nearest its preferred range from the
        /// threat; with <paramref name="away"/>, cover further from the threat than it is now (retreating). Cover a
        /// squadmate has taken is left for them.
        /// </summary>
        public static bool TryPick(IAIWorld world, Vector3 self, Vector3 threat, float preferredRange, bool away,
            SquadMemory squad, string memberId, out CoverSpot best)
        {
            best = default;
            if (world == null)
            {
                return false;
            }

            var found = false;
            var bestScore = float.MaxValue;
            var selfToThreat = Vector3.Distance(self, threat);
            foreach (var spot in world.CoverNear(self, SearchRadius))
            {
                var toSelf = Vector3.Distance(spot.Position, self);
                var toThreat = Vector3.Distance(spot.Position, threat);
                if (toThreat < 5f || toSelf >= toThreat || (away && toThreat < selfToThreat + 3f))
                {
                    continue;
                }

                if ((squad != null && squad.IsCoverTaken(spot.Position, memberId)) || !Protects(spot, threat, world))
                {
                    continue;
                }

                var score = away ? toSelf - toThreat * 0.5f : toSelf + Math.Abs(toThreat - preferredRange) * 0.5f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = spot;
                    found = true;
                }
            }

            return found;
        }

        internal static Vector3 Flat(Vector3 v) => new Vector3(v.X, 0f, v.Z);

        internal static float AngleBetween(Vector3 a, Vector3 b)
        {
            var dot = Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b));
            return (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, dot))) * 180.0 / Math.PI);
        }
    }

    /// <summary>
    /// An AI's brain: a state machine, ticked by its body a few times a second on the server, that reads what it
    /// knows (<see cref="AIPerception"/>, ticked first) and tells its <see cref="IAIBody"/> where to go, where to
    /// look and when to shoot. Plain C#, so it's tested with a fake body.
    ///
    /// <list type="bullet">
    /// <item><b>Patrol</b>: walk its route (in order, or wandering between its points), stopping at each; with no
    /// route, stand guard where it spawned and look about.</item>
    /// <item><b>Investigate</b>: something it glimpsed, heard (a shot, or being hit) or its squad saw: walk there
    /// (run, if it was hit or its squad called it), look around, and go back to patrolling.</item>
    /// <item><b>Attack</b>: alert to a target. Once a second it scores push, hold, reposition and reload
    /// (<see cref="AttackScoring"/>) and does the best; it shoots, after its reaction time, while it can see the
    /// target and has a line of fire, with an aim that tightens as it tracks (<see cref="AIAim"/>). Out of sight
    /// for <see cref="LoseTargetSeconds"/>, it searches.</item>
    /// <item><b>Search</b>: go to where the target was last seen, check the cover nearby, give up after
    /// <see cref="SearchSeconds"/>.</item>
    /// <item><b>Retreat</b>: hurt below its courage, or out of ammo: get to cover away from the threat (or just
    /// away), reload, wait, then search (or, still hurt, go back to its post). Cornered, it fights.</item>
    /// </list>
    /// </summary>
    public sealed class AIBrain
    {
        public const double DecideEverySeconds = 1;
        public const double LoseTargetSeconds = 4;
        public const double LookAroundSeconds = 6;
        public const double SearchSeconds = 25;
        public const double SearchLookSeconds = 3;
        public const double RetreatWaitSeconds = 6;

        /// <summary>After retreating, how long before being hurt sends it back again (otherwise it would never fight again, hurt).</summary>
        public const double RetreatCooldownSeconds = 30;

        /// <summary>A retreating AI with its target this close fights rather than run.</summary>
        public const float CorneredDistance = 8f;

        public const float ArriveDistance = 1.2f;
        public const float EyeHeight = 1.55f;
        public const float ChestHeight = 1.15f;

        private readonly IAIBody _body;
        private readonly AIPerception _perception;
        private readonly AIBrainSettings _settings;
        private readonly IAIWorld _world;
        private readonly List<Vector3> _route;
        private readonly bool _loop;
        private readonly Random _random;
        private readonly Vector3 _home;
        private readonly Vector3 _homeFacing;

        private Vector3? _moveTarget;
        private bool _moveRun;
        private int _routeIndex;
        private int _routeStep = 1;
        private double _pausedUntil = double.MinValue;
        private double _lookAroundUntil = double.MinValue;
        private double _nextGlanceAt;
        private Vector3 _glance;

        private Vector3 _investigatePoint;
        private bool _investigateRun;
        private double _handledHeard = double.MinValue;
        private double _handledSquad = double.MinValue;

        private double _attackSince;
        private double _trackingSince;
        private double _nextDecideAt;
        private bool _reloadAsked;
        private double _burstUntil = double.MinValue;
        private double _pauseUntil = double.MinValue;
        private CoverSpot? _cover;

        private List<Vector3> _searchPoints = new List<Vector3>();
        private int _searchIndex;
        private double _searchSince;
        private double _searchLookUntil = double.MinValue;

        private double _retreatedAt = double.MinValue;
        private double _retreatArrivedAt = double.MinValue;

        public AIBrain(string id, IAIBody body, AIPerception perception, AIBrainSettings settings = null, IAIWorld world = null,
            IReadOnlyList<Vector3> route = null, bool loopRoute = true, int seed = 0)
        {
            Id = id;
            _body = body ?? throw new ArgumentNullException(nameof(body));
            _perception = perception ?? throw new ArgumentNullException(nameof(perception));
            _settings = settings ?? new AIBrainSettings();
            _world = world;
            _route = route != null ? new List<Vector3>(route) : new List<Vector3>();
            _loop = loopRoute;
            _random = new Random(seed);
            _home = body.Position;
            _homeFacing = CoverPicker.Flat(body.Forward).LengthSquared() > 0.0001f ? Vector3.Normalize(CoverPicker.Flat(body.Forward)) : Vector3.UnitZ;
            Squad?.Join(id);
        }

        public string Id { get; }

        public AIState State { get; private set; } = AIState.Patrol;

        /// <summary>In Attack, what it's doing about its target.</summary>
        public AttackAction Action { get; private set; }

        /// <summary>Who it's fighting, searching for or retreating from; null when patrolling or investigating.</summary>
        public string Target { get; private set; }

        /// <summary>Where it's going, if anywhere.</summary>
        public Vector3? MoveTarget => _moveTarget;

        /// <summary>The cover it's taken or heading for, if any.</summary>
        public CoverSpot? Cover => _cover;

        /// <summary>Whether it's holding the trigger.</summary>
        public bool Firing { get; private set; }

        public AIBrainSettings Settings => _settings;

        private SquadMemory Squad => _perception.Squad;

        /// <summary>It was shot, by someone (if known) from somewhere (if known): it hears it, its squad learns where the shooter is, and it comes running.</summary>
        public void WasHit(double now, string shooterId, Vector3? from)
        {
            if (State == AIState.Dead)
            {
                return;
            }

            var where = from ?? _body.Position;
            _perception.Hear(now, where);
            if (shooterId != null && from.HasValue)
            {
                Squad?.Report(shooterId, from.Value, now);
            }

            if (State == AIState.Patrol || State == AIState.Search || State == AIState.Investigate)
            {
                StartInvestigating(now, where, run: true);
                _handledHeard = now;
            }
        }

        /// <summary>One tick: after perception's.</summary>
        public void Tick(double now)
        {
            if (State == AIState.Dead)
            {
                return;
            }

            if (_body.HealthFraction <= 0f)
            {
                State = AIState.Dead;
                Action = AttackAction.None;
                SetTrigger(false);
                _body.Stop();
                _moveTarget = null;
                Squad?.Leave(Id);
                return;
            }

            switch (State)
            {
                case AIState.Attack:
                    TickAttack(now);
                    break;
                case AIState.Retreat:
                    TickRetreat(now);
                    break;
                default:
                    if (!React(now))
                    {
                        if (State == AIState.Patrol)
                        {
                            TickPatrol(now);
                        }
                        else if (State == AIState.Investigate)
                        {
                            TickInvestigate(now);
                        }
                        else
                        {
                            TickSearch(now);
                        }
                    }

                    break;
            }

            Squad?.SetActivity(Id, State, Target);
        }

        /// <summary>A line for the debug view: "Attack: hold (cover), firing", "Patrol: to point 2".</summary>
        public string Describe()
        {
            switch (State)
            {
                case AIState.Attack:
                    return "Attack: " + Action.ToString().ToLowerInvariant() + (InCover() ? " (in cover)" : "") + (Firing ? ", firing" : "");
                case AIState.Patrol:
                    return _route.Count == 0 ? "Patrol: guarding" : "Patrol: point " + (_routeIndex + 1) + " of " + _route.Count;
                case AIState.Search:
                    return "Search: " + (_searchIndex + 1) + " of " + _searchPoints.Count;
                case AIState.Retreat:
                    return "Retreat" + (_cover.HasValue ? " to cover" : "");
                default:
                    return State.ToString();
            }
        }

        // ---- Calm states: what they react to ----

        // Alert: fight. Noticed someone: go and look. A new noise, or a new squad sighting: go and look. True if it
        // changed what it's doing (or kept investigating something new).
        private bool React(double now)
        {
            if (_perception.IsAlert)
            {
                StartAttack(now, _perception.Focus);
                return true;
            }

            if (_perception.Focus != null && _perception.TryGetLastSeen(_perception.Focus, out var glimpse, out _))
            {
                if (State != AIState.Investigate || Vector3.Distance(glimpse, _investigatePoint) > 2f)
                {
                    StartInvestigating(now, glimpse, run: false);
                }

                TickInvestigate(now);
                return true;
            }

            var heard = _perception.Heard;
            if (heard.HasValue && heard.Value.Time > _handledHeard && now - heard.Value.Time <= AIPerception.HeardForSeconds)
            {
                _handledHeard = heard.Value.Time;
                StartInvestigating(now, heard.Value.Position, run: false);
                TickInvestigate(now);
                return true;
            }

            if (Squad != null && Squad.TryGetLatest(now, AIPerception.HeardForSeconds, out _, out var called, out var calledAt) && calledAt > _handledSquad)
            {
                _handledSquad = calledAt;
                if (State != AIState.Search)
                {
                    StartInvestigating(now, called, run: true);
                    TickInvestigate(now);
                    return true;
                }
            }

            return false;
        }

        private void TickPatrol(double now)
        {
            if (_route.Count == 0)
            {
                if (Flat(_home - _body.Position).Length() > ArriveDistance)
                {
                    Go(_home, run: false);
                    _body.LookAt(_home + Vector3.UnitY * EyeHeight);
                }
                else
                {
                    StopMoving();
                    LookAbout(now, _body.Position, _homeFacing);
                }

                return;
            }

            if (now < _pausedUntil)
            {
                LookAbout(now, _body.Position, _homeFacing);
                return;
            }

            var point = _route[_routeIndex];
            if (Reached(point) || _body.IsStuck)
            {
                StopMoving();
                _pausedUntil = now + _settings.PatrolPauseSeconds;
                NextRoutePoint();
                return;
            }

            Go(point, run: false);
            _body.LookAt(point + Vector3.UnitY * EyeHeight);
        }

        private void NextRoutePoint()
        {
            if (_route.Count < 2)
            {
                return;
            }

            if (_settings.Wanders)
            {
                var next = _random.Next(_route.Count - 1);
                _routeIndex = next >= _routeIndex ? next + 1 : next; // anywhere but here
            }
            else if (_loop)
            {
                _routeIndex = (_routeIndex + 1) % _route.Count;
            }
            else
            {
                if (_routeIndex + _routeStep < 0 || _routeIndex + _routeStep >= _route.Count)
                {
                    _routeStep = -_routeStep; // there and back
                }

                _routeIndex += _routeStep;
            }
        }

        private void StartInvestigating(double now, Vector3 point, bool run)
        {
            State = AIState.Investigate;
            Action = AttackAction.None;
            Target = null;
            _investigatePoint = point;
            _investigateRun = run;
            _lookAroundUntil = double.MinValue;
            ReleaseCover();
            SetTrigger(false);
        }

        private void TickInvestigate(double now)
        {
            if (_lookAroundUntil == double.MinValue)
            {
                if (!Reached(_investigatePoint) && !_body.IsStuck)
                {
                    Go(_investigatePoint, _investigateRun);
                    _body.LookAt(_investigatePoint + Vector3.UnitY * EyeHeight);
                    return;
                }

                StopMoving();
                _lookAroundUntil = now + LookAroundSeconds;
            }

            LookAbout(now, _body.Position, Flat(_investigatePoint - _home));
            if (now >= _lookAroundUntil)
            {
                BackToPatrol();
            }
        }

        private void StartSearch(double now, Vector3 lastKnown)
        {
            State = AIState.Search;
            Action = AttackAction.None;
            SetTrigger(false);
            ReleaseCover();
            _searchSince = now;
            _searchIndex = 0;
            _searchLookUntil = double.MinValue;
            _searchPoints = new List<Vector3> { lastKnown };
            if (_world != null)
            {
                var near = new List<CoverSpot>(_world.CoverNear(lastKnown, 15f));
                near.Sort((a, b) => Vector3.Distance(a.Position, lastKnown).CompareTo(Vector3.Distance(b.Position, lastKnown)));
                for (int i = 0; i < near.Count && i < 2; i++)
                {
                    _searchPoints.Add(near[i].Position);
                }
            }
        }

        private void TickSearch(double now)
        {
            if (now - _searchSince > SearchSeconds || _searchIndex >= _searchPoints.Count)
            {
                BackToPatrol();
                return;
            }

            var point = _searchPoints[_searchIndex];
            if (_searchLookUntil == double.MinValue)
            {
                if (!Reached(point) && !_body.IsStuck)
                {
                    Go(point, run: false);
                    _body.LookAt(point + Vector3.UnitY * EyeHeight);
                    return;
                }

                StopMoving();
                _searchLookUntil = now + SearchLookSeconds;
            }

            LookAbout(now, _body.Position, Flat(point - _home));
            if (now >= _searchLookUntil)
            {
                _searchIndex++;
                _searchLookUntil = double.MinValue;
            }
        }

        private void BackToPatrol()
        {
            State = AIState.Patrol;
            Action = AttackAction.None;
            Target = null;
            SetTrigger(false);
            ReleaseCover();
            _pausedUntil = double.MinValue;
        }

        // ---- Attack ----

        private void StartAttack(double now, string target)
        {
            State = AIState.Attack;
            Target = target;
            Action = AttackAction.None;
            _attackSince = now;
            _trackingSince = now;
            _nextDecideAt = now;
            _reloadAsked = false;
        }

        private void TickAttack(double now)
        {
            if (_perception.IsAlert && _perception.Focus != Target)
            {
                Target = _perception.Focus; // someone it's more aware of: a new target, a new reaction
                _attackSince = now;
                _trackingSince = now;
                _nextDecideAt = now;
            }

            if (!_perception.TryGetLastSeen(Target, out var seenAt, out var seenTime))
            {
                BackToPatrol();
                return;
            }

            var inView = now - seenTime <= 0.5;
            if (!inView)
            {
                _trackingSince = now; // its aim settles again when it sees them again
                if (now - seenTime > LoseTargetSeconds)
                {
                    StartSearch(now, seenAt);
                    return;
                }
            }

            var outOfAmmo = _body.LoadedRounds == 0 && !_body.CanReload && !_body.IsReloading;
            if (outOfAmmo || (_body.HealthFraction < _settings.Courage && now - _retreatedAt > RetreatCooldownSeconds))
            {
                StartRetreat(now, seenAt);
                TickRetreat(now);
                return;
            }

            var chest = seenAt + Vector3.UnitY * ChestHeight;
            var distance = Vector3.Distance(_body.Position, seenAt);
            var actionDone = (Action == AttackAction.Reposition && (!_cover.HasValue || Reached(_cover.Value.Position) || _body.IsStuck))
                || (Action == AttackAction.Reload && _reloadAsked && !_body.IsReloading)
                || (Action == AttackAction.Push && (_body.IsStuck || (_moveTarget.HasValue && Reached(_moveTarget.Value))));
            if (now >= _nextDecideAt || actionDone)
            {
                Decide(now, seenAt, distance, inView, chest);
            }

            switch (Action)
            {
                case AttackAction.Push:
                    Go(PushPoint(seenAt), run: distance > _settings.PreferredRange * 1.5f);
                    break;
                case AttackAction.Reposition:
                    if (_cover.HasValue)
                    {
                        Go(_cover.Value.Position, run: true);
                    }

                    break;
                case AttackAction.Reload:
                    StopMoving();
                    if (!_reloadAsked)
                    {
                        _body.Reload();
                        _reloadAsked = true;
                    }

                    break;
                default:
                    StopMoving();
                    break;
            }

            var settled = now - _trackingSince;
            var radius = AIAim.ErrorRadius(_settings, distance, settled, _perception.SpeedOf(Target));
            _body.LookAt(inView ? AIAim.Around(chest, radius, _random) : chest);

            var mayFire = inView && Action != AttackAction.Reload && !_body.IsReloading && _body.LoadedRounds > 0
                && now - _attackSince >= _settings.ReactionSeconds && _body.HasLineOfFire(chest);
            Burst(now, mayFire);
        }

        private void Decide(double now, Vector3 threat, float distance, bool inView, Vector3 chest)
        {
            _nextDecideAt = now + DecideEverySeconds;
            var inCover = InCover() && CoverPicker.Protects(_cover.Value, threat, _world);
            var coverAvailable = CoverPicker.TryPick(_world, _body.Position, threat, _settings.PreferredRange, away: false, Squad, Id, out var spot)
                && (!_cover.HasValue || Vector3.Distance(spot.Position, _cover.Value.Position) > 1f || !inCover);
            var allies = 1 + (Squad?.CountIn(AIState.Attack, null, Id) ?? 0);
            var hostiles = Math.Max(1, Squad?.KnownHostiles(now, 30) ?? 1);
            var situation = new AttackSituation
            {
                Distance = distance,
                InView = inView,
                HasLineOfFire = _body.HasLineOfFire(chest),
                InCover = inCover,
                CoverAvailable = coverAvailable,
                LoadedRounds = _body.LoadedRounds,
                Capacity = _body.Capacity,
                CanReload = _body.CanReload,
                HealthFraction = _body.HealthFraction,
                Advantage = (float)allies / hostiles,
                Current = Action,
            };

            var next = AttackScoring.Choose(situation, _settings);
            if (next == AttackAction.Reposition)
            {
                _cover = spot;
                Squad?.ClaimCover(Id, spot.Position);
            }
            else if (next == AttackAction.Push)
            {
                ReleaseCover();
            }

            if (next == AttackAction.Reload && Action != AttackAction.Reload)
            {
                _reloadAsked = false;
            }

            Action = next;
        }

        // Where pushing goes: to its preferred range (a bit inside it), straight at the target; or, for an AI that
        // flanks with a squadmate already on the target, round to one side.
        private Vector3 PushPoint(Vector3 threat)
        {
            var fromThreat = Flat(_body.Position - threat);
            if (fromThreat.LengthSquared() < 0.01f)
            {
                return _body.Position;
            }

            fromThreat = Vector3.Normalize(fromThreat);
            var range = _settings.PreferredRange * 0.6f;
            if (_settings.Flanks && Squad != null && Squad.CountIn(AIState.Attack, Target, Id) > 0)
            {
                var side = (Id.GetHashCode() & 1) == 0 ? 1f : -1f;
                var turn = Matrix4x4.CreateRotationY(side * 75f * (float)Math.PI / 180f);
                return threat + Vector3.TransformNormal(fromThreat, turn) * range;
            }

            return Vector3.Distance(_body.Position, threat) <= range ? _body.Position : threat + fromThreat * range;
        }

        private void Burst(double now, bool mayFire)
        {
            if (!mayFire)
            {
                SetTrigger(false);
                return;
            }

            if (now < _burstUntil)
            {
                SetTrigger(true);
            }
            else if (now < _pauseUntil)
            {
                SetTrigger(false);
            }
            else
            {
                _burstUntil = now + 0.25 + _random.NextDouble() * 0.3;
                _pauseUntil = _burstUntil + 0.3 + _random.NextDouble() * 0.5;
                SetTrigger(true);
            }
        }

        // ---- Retreat ----

        private void StartRetreat(double now, Vector3 threat)
        {
            State = AIState.Retreat;
            Action = AttackAction.None;
            SetTrigger(false);
            _retreatedAt = now;
            _retreatArrivedAt = double.MinValue;
            _threat = threat;
            _reloadAsked = false;
            ReleaseCover();
            if (CoverPicker.TryPick(_world, _body.Position, threat, _settings.PreferredRange, away: true, Squad, Id, out var spot))
            {
                _cover = spot;
                Squad?.ClaimCover(Id, spot.Position);
            }
        }

        private void TickRetreat(double now)
        {
            if (_perception.TryGetLastSeen(Target, out var seen, out var seenTime))
            {
                _threat = seen;
            }

            var threat = _threat;
            if (_perception.IsAlert && now - seenTime <= 0.5 && Vector3.Distance(_body.Position, threat) < CorneredDistance)
            {
                StartAttack(now, _perception.Focus); // cornered
                return;
            }

            var goal = _cover.HasValue ? _cover.Value.Position : AwayFrom(threat);
            if (_retreatArrivedAt == double.MinValue)
            {
                if (!Reached(goal) && !_body.IsStuck)
                {
                    Go(goal, run: true);
                    _body.LookAt(threat + Vector3.UnitY * ChestHeight);
                    return;
                }

                StopMoving();
                _retreatArrivedAt = now;
            }

            if (!_reloadAsked && _body.CanReload && _body.LoadedRounds < _body.Capacity)
            {
                _body.Reload();
                _reloadAsked = true;
            }

            _body.LookAt(threat + Vector3.UnitY * ChestHeight);
            if (now - _retreatArrivedAt >= RetreatWaitSeconds && !_body.IsReloading)
            {
                if (_body.HealthFraction < _settings.Courage || (_body.LoadedRounds == 0 && !_body.CanReload))
                {
                    BackToPatrol();
                }
                else
                {
                    StartSearch(now, threat);
                }
            }
        }

        private Vector3 _threat;
        private Vector3 _awayPoint;
        private double _awayChosenAt = double.MinValue;

        // 20 m straight away from the threat, chosen once per retreat.
        private Vector3 AwayFrom(Vector3 threat)
        {
            if (_awayChosenAt != _retreatedAt)
            {
                _awayChosenAt = _retreatedAt;
                var away = Flat(_body.Position - threat);
                away = away.LengthSquared() < 0.01f ? -_homeFacing : Vector3.Normalize(away);
                _awayPoint = _body.Position + away * 20f;
            }

            return _awayPoint;
        }

        // ---- The body ----

        private void Go(Vector3 point, bool run)
        {
            if (!_moveTarget.HasValue || Vector3.Distance(_moveTarget.Value, point) > 1f || _moveRun != run)
            {
                _moveTarget = point;
                _moveRun = run;
                _body.MoveTo(point, run);
            }
        }

        private void StopMoving()
        {
            if (_moveTarget.HasValue)
            {
                _moveTarget = null;
                _body.Stop();
            }
        }

        private bool Reached(Vector3 point) => Flat(point - _body.Position).Length() <= ArriveDistance;

        private bool InCover() => _cover.HasValue && Flat(_cover.Value.Position - _body.Position).Length() <= ArriveDistance;

        private void ReleaseCover()
        {
            if (_cover.HasValue)
            {
                _cover = null;
                Squad?.ReleaseCover(Id);
            }
        }

        private void SetTrigger(bool held)
        {
            if (Firing != held)
            {
                Firing = held;
                _body.SetTrigger(held);
            }
        }

        // Glances about, every couple of seconds, within 70° of a direction.
        private void LookAbout(double now, Vector3 from, Vector3 around)
        {
            if (now >= _nextGlanceAt)
            {
                _nextGlanceAt = now + 1.5 + _random.NextDouble() * 1.5;
                var centre = Flat(around).LengthSquared() > 0.0001f ? Vector3.Normalize(Flat(around)) : _homeFacing;
                var turn = Matrix4x4.CreateRotationY((float)((_random.NextDouble() * 2 - 1) * 70 * Math.PI / 180));
                _glance = Vector3.TransformNormal(centre, turn);
            }

            _body.LookAt(from + _glance * 10f + Vector3.UnitY * EyeHeight);
        }

        private static Vector3 Flat(Vector3 v) => CoverPicker.Flat(v);
    }
}
