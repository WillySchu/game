using System.Collections.Generic;
using FishNet.Object;
using RaidZero.Items;
using RaidZero.Raid;
using RaidZero.Shared.Raid;
using UnityEngine;

namespace RaidZero.Shared
{
    /// <summary>
    /// A kind of AI: who it's with, what it is in the world, and what it carries. An <see cref="AISpawnZone"/> names
    /// one. Its sight and how it fights (the brain's settings) are here too; how it fights defaults to its faction's.
    /// </summary>
    [CreateAssetMenu(menuName = "Raid Zero/AI/Profile", fileName = "AIProfile")]
    public sealed class AIProfile : ScriptableObject
    {
        [Tooltip("Who it's with: friendly to its own faction, hostile to everyone else and to every player.")]
        [SerializeField] private Faction _faction = Faction.Looters;

        [Tooltip("What spawns: a spawnable network prefab with an IRaidAI on it. The target dummy, until the AI body exists.")]
        [SerializeField] private NetworkObject _prefab;

        [Tooltip("What it can wear and carry, picked by each kit's weight.")]
        [SerializeField] private List<AIKitAsset> _kits = new List<AIKitAsset>();

        [Tooltip("Extra loot on top of the kit, rolled for each AI and packed in its rig and backpack. Optional.")]
        [SerializeField] private LootTableAsset _extraLoot;

        [Header("Sight")]
        [Tooltip("Beyond this, in metres, it sees nothing.")]
        [Min(1f)]
        [SerializeField] private float _viewDistance = 80f;

        [Tooltip("The whole width of its view, in degrees: someone outside it isn't seen at all.")]
        [Range(10f, 360f)]
        [SerializeField] private float _viewAngle = 120f;

        [Tooltip("How fast it becomes aware of someone in view, per second, at best: close, dead ahead and moving. 1 is about a second; 0.5 two.")]
        [Min(0.01f)]
        [SerializeField] private float _awarenessRate = 1f;

        [Tooltip("How fast it loses track of someone out of sight, per second: 0.2 forgets them in five seconds.")]
        [Min(0.01f)]
        [SerializeField] private float _forgetRate = 0.2f;

        [Header("How it fights")]
        [Tooltip("Off: its faction's defaults (looters push with the numbers and break off when hurt; contractors hold cover, flank and stay in). On: the settings below.")]
        [SerializeField] private bool _customBrain;

        [Tooltip("Between becoming alert and its first shot, in seconds.")]
        [Min(0f)]
        [SerializeField] private float _reactionSeconds = 0.6f;

        [Tooltip("How far off its aim is at 20 m, in metres, before it's settled on a target. On top of the gun's spread.")]
        [Min(0f)]
        [SerializeField] private float _aimErrorAt20m = 1.2f;

        [Tooltip("How long tracking one target takes its aim from wide to tight (a third as wide), in seconds.")]
        [Min(0.1f)]
        [SerializeField] private float _aimSettleSeconds = 2f;

        [Tooltip("How much it likes each choice in a fight: closing in, holding and shooting, moving to cover, reloading early.")]
        [Min(0f)] [SerializeField] private float _pushWeight = 1f;
        [Min(0f)] [SerializeField] private float _holdWeight = 1f;
        [Min(0f)] [SerializeField] private float _repositionWeight = 1f;
        [Min(0f)] [SerializeField] private float _reloadWeight = 1f;

        [Tooltip("How much having the numbers makes it push: 0 not at all, 1 a lot.")]
        [Range(0f, 2f)]
        [SerializeField] private float _numbersMatter = 0.5f;

        [Tooltip("Whether it goes round the side when a squadmate is already fighting the same target.")]
        [SerializeField] private bool _flanks;

        [Tooltip("Below this much of its health it retreats: 0.5 at half health, 0 never.")]
        [Range(0f, 1f)]
        [SerializeField] private float _courage = 0.35f;

        [Tooltip("The distance it likes to fight at, in metres.")]
        [Min(1f)]
        [SerializeField] private float _preferredRange = 20f;

        [Tooltip("On: it visits its route's points in any order (looters between loot spots). Off: in order (a patrol).")]
        [SerializeField] private bool _wanders;

        [Tooltip("How long it stops at each point of its route, in seconds.")]
        [Min(0f)]
        [SerializeField] private float _patrolPauseSeconds = 4f;

        public Faction Faction => _faction;

        /// <summary>How it fights: its faction's defaults, or the profile's own settings.</summary>
        public AIBrainSettings Brain => !_customBrain ? AIBrainSettings.For(_faction) : new AIBrainSettings
        {
            ReactionSeconds = _reactionSeconds,
            AimErrorAt20m = _aimErrorAt20m,
            AimSettleSeconds = _aimSettleSeconds,
            PushWeight = _pushWeight,
            HoldWeight = _holdWeight,
            RepositionWeight = _repositionWeight,
            ReloadWeight = _reloadWeight,
            NumbersMatter = _numbersMatter,
            Flanks = _flanks,
            Courage = _courage,
            PreferredRange = _preferredRange,
            Wanders = _wanders,
            PatrolPauseSeconds = _patrolPauseSeconds,
        };

        /// <summary>How it sees (AIPerception).</summary>
        public SightSettings Sight => new SightSettings
        {
            ViewDistance = _viewDistance,
            ViewAngle = _viewAngle,
            FillRate = _awarenessRate,
            DrainRate = _forgetRate,
        };
        public NetworkObject Prefab => _prefab;
        public IReadOnlyList<AIKitAsset> Kits => _kits;
        public LootTableAsset ExtraLoot => _extraLoot;

#if UNITY_EDITOR
        /// <summary>For the example assets menu.</summary>
        public void Set(Faction faction, NetworkObject prefab, IEnumerable<AIKitAsset> kits, LootTableAsset extraLoot)
        {
            _faction = faction;
            _prefab = prefab;
            _kits = new List<AIKitAsset>(kits);
            _extraLoot = extraLoot;
        }
#endif
    }
}
