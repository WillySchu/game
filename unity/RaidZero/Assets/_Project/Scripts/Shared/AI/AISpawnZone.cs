using System.Collections.Generic;
using RaidZero.Net;
using RaidZero.Raid;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Loot;
using RaidZero.Shared.Raid;
using UnityEngine;

namespace RaidZero.Shared
{
    /// <summary>
    /// Where a map's AI spawn: a box on the map (drawn in the Scene view), a profile, how many (a range), and for
    /// patrols a route. When a raid starts (its first player joins), the server rolls each zone, from the raid's
    /// loot seed and the zone's place in the scene, so the same seed spawns the same AI with the same gear: how
    /// many, and which of the profile's kits each wears, with its extra loot. Each AI goes on the ground at a
    /// random spot in the box that no player can see from where they are or where players spawn, and no nearer
    /// to one than <see cref="_minPlayerDistance"/>. If there isn't such a spot, it doesn't spawn. More AI
    /// arriving mid-raid comes later.
    /// </summary>
    public sealed class AISpawnZone : MonoBehaviour
    {
        private const int TriesPerAI = 12;
        private const float Spacing = 1.5f;

        [SerializeField] private AIProfile _profile;

        [Min(0)]
        [SerializeField] private int _minCount = 1;

        [Min(0)]
        [SerializeField] private int _maxCount = 2;

        [Tooltip("The box AI spawn in, centred on this object and turned with it. They're put on whatever ground is inside it.")]
        [SerializeField] private Vector3 _size = new Vector3(10f, 6f, 10f);

        [Tooltip("For patrols: the route they walk. Optional.")]
        [SerializeField] private PatrolRoute _route;

        [Tooltip("No AI spawns nearer than this to a player, or to where players spawn.")]
        [Min(0)]
        [SerializeField] private float _minPlayerDistance = 30f;

        private bool _spawned;

        public AIProfile Profile => _profile;
        public PatrolRoute Route => _route;

        // Its seed: the raid's, with its place in the scene, as crates do.
        private string ZoneKey => gameObject.scene.path + ":" + HierarchyPath(transform);

        // Server only: RaidServer.Instance is only running there.
        private void Update()
        {
            var raid = RaidServer.Instance;
            if (raid == null || !raid.IsRunning)
            {
                return;
            }

            if (raid.Phase == RaidPhase.Waiting)
            {
                _spawned = false; // the next raid, if this map stays loaded
            }
            else if (raid.Phase == RaidPhase.InProgress && !_spawned)
            {
                _spawned = true;
                Spawn(raid);
            }
        }

        private void Spawn(RaidServer raid)
        {
            if (_profile == null || _profile.Prefab == null)
            {
                Debug.LogError("[ai] Spawn zone " + name + " has no profile, or its profile no prefab, so nothing spawns there.", this);
                return;
            }

            var kits = new List<AIKitAsset>();
            var weights = new List<int>();
            foreach (var kit in _profile.Kits)
            {
                if (kit != null)
                {
                    kits.Add(kit);
                    weights.Add(kit.Weight);
                }
            }

            var random = LootRandom.ForContainer(raid.LootSeed, ZoneKey);
            int count = AISpawnRolls.Count(random, _minCount, _maxCount);
            var extras = _profile.ExtraLoot != null ? _profile.ExtraLoot.ToTable() : null;
            var placed = new List<Vector3>();
            for (int i = 0; i < count; i++)
            {
                var pick = AISpawnRolls.PickWeighted(random, weights);
                if (pick < 0)
                {
                    Debug.LogError("[ai] " + _profile.name + " has no kits with any weight, so spawn zone " + name + " spawns nothing.", this);
                    return;
                }

                if (!TryFindPlace(raid, random, placed, out var place))
                {
                    Debug.LogWarning("[ai] Spawn zone " + name + " found nowhere out of players' sight for AI " + (i + 1) + " of " + count + ".", this);
                    continue;
                }

                ItemNode character;
                var leftOut = new List<ItemNode>();
                try
                {
                    character = AIKits.Build(raid.Inventory.Catalog, kits[pick].ToSpec(), extras, random, Uuid7.NewId, leftOut);
                }
                catch (ItemSpecException exception)
                {
                    Debug.LogError("[ai] " + kits[pick].name + " can't be built: " + exception.Message, kits[pick]);
                    return;
                }

                if (leftOut.Count > 0)
                {
                    Debug.LogWarning("[ai] " + kits[pick].name + " plus its extra loot didn't fit: " + leftOut.Count + " item(s) left out.", kits[pick]);
                }

                placed.Add(place);
                var facing = Quaternion.Euler(0f, (float)random.Range(0.0, 360.0), 0f);
                raid.SpawnAI(_profile.Prefab, place, facing, character, spawned =>
                {
                    var dummy = spawned.GetComponent<TargetDummy>();
                    if (dummy != null)
                    {
                        dummy.Assign(_profile, this);
                    }
                });
            }
        }

        // A spot on the ground inside the box, out of players' sight, not on top of another AI from here.
        private bool TryFindPlace(RaidServer raid, LootRandom random, List<Vector3> placed, out Vector3 place)
        {
            for (int attempt = 0; attempt < TriesPerAI; attempt++)
            {
                var local = new Vector3(
                    (float)random.Range(-0.5, 0.5) * _size.x,
                    _size.y * 0.5f,
                    (float)random.Range(-0.5, 0.5) * _size.z);
                var top = transform.TransformPoint(local);
                if (!Physics.Raycast(top, Vector3.down, out var hit, _size.y + 1f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                bool crowded = false;
                foreach (var other in placed)
                {
                    crowded |= Vector3.Distance(other, hit.point) < Spacing;
                }

                if (!crowded && raid.IsHiddenFromPlayers(hit.point, _minPlayerDistance))
                {
                    place = hit.point;
                    return true;
                }
            }

            place = default;
            return false;
        }

        private static string HierarchyPath(Transform t) => t.parent == null ? t.name : HierarchyPath(t.parent) + "/" + t.name;

        private void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = _profile == null ? Color.grey : _profile.Faction == Faction.Contractors ? new Color(0.35f, 0.65f, 1f) : new Color(1f, 0.8f, 0.2f);
            Gizmos.DrawWireCube(Vector3.zero, _size);
            Gizmos.matrix = Matrix4x4.identity;
#if UNITY_EDITOR
            UnityEditor.Handles.Label(transform.position + Vector3.up * (_size.y * 0.5f),
                (_profile != null ? _profile.name : "no profile") + " ×" + _minCount + (_maxCount > _minCount ? "–" + _maxCount : ""));
#endif
        }
    }
}
