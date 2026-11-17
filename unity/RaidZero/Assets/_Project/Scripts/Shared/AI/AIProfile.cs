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
    /// one. More fields (reaction time, aim, attack weights, courage) come with the brain.
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

        public Faction Faction => _faction;

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
