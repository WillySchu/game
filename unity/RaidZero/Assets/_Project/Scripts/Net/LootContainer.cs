using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Items;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Loot;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// Where each container is in the world, by container ID, so the server can check a player is close
    /// enough to loot it. Crates register themselves (<see cref="LootContainer"/>); bodies and piles on the
    /// ground should register when they're spawned and unregister when they go.
    /// </summary>
    public static class ContainerRegistry
    {
        private static readonly Dictionary<string, Transform> Places = new Dictionary<string, Transform>(StringComparer.Ordinal);

        public static void Register(string containerId, Transform place)
        {
            if (Places.TryGetValue(containerId, out var existing) && existing != null && existing != place)
            {
                Debug.LogError("Two objects claim container " + containerId + ": " + existing.name + " and " + place.name + ". Give crates unique names.", place);
                return;
            }

            Places[containerId] = place;
        }

        public static void Unregister(string containerId, Transform place)
        {
            if (Places.TryGetValue(containerId, out var existing) && existing == place)
            {
                Places.Remove(containerId);
            }
        }

        public static bool TryGetPosition(string containerId, out Vector3 position)
        {
            if (containerId != null && Places.TryGetValue(containerId, out var place) && place != null)
            {
                position = place.position;
                return true;
            }

            position = default;
            return false;
        }
    }

    /// <summary>
    /// A crate or other loot container placed in a level. The server fills it when the raid starts.
    ///
    /// Its ID comes from the scene and its place in the hierarchy, so the server and every client work out
    /// the same ID without sending it: a client's interaction code reads <see cref="ContainerId"/> and passes
    /// it to RaidInventoryClient.Open. Keep sibling crates' names unique.
    ///
    /// What's in it comes from a loot table rolled with the raid's seed: this crate's own table if it names one,
    /// otherwise the default table on its container type's item asset. For testing, a list of fixed contents
    /// overrides both.
    /// </summary>
    public sealed class LootContainer : MonoBehaviour
    {
        [Tooltip("The container's item definition, which sets its grid size.")]
        [SerializeField]
        private string _defId = "crate_wooden";

        [Tooltip("This crate's own loot table. Leave empty to use the default table on the container type's item asset.")]
        [SerializeField]
        private LootTableAsset _lootTable = null;

        [Tooltip("For testing: exactly these items every raid, instead of any loot table. Leave empty to use the tables.")]
        [SerializeField]
        private List<LootEntry> _contents = new List<LootEntry>();

        public string ContainerId { get; private set; }

        /// <summary>The loot table this crate rolls: its own, or its container type's default. Null if neither is set.</summary>
        public LootTableAsset LootTable
        {
            get
            {
                if (_lootTable != null)
                {
                    return _lootTable;
                }

                var type = ItemLibrary.Instance != null ? ItemLibrary.Instance.Find(_defId) : null;
                return type != null ? type.DefaultLoot : null;
            }
        }

        /// <summary>
        /// The container and fresh items for one raid: the fixed contents if any are listed, otherwise the loot
        /// table rolled with <paramref name="raidSeed"/>. Items that don't fit are left out, with a warning.
        /// </summary>
        public ItemNode BuildContents(ItemCatalog catalog, int raidSeed)
        {
            if (_contents.Count > 0)
            {
                return BuildFixedContents(catalog);
            }

            var asset = LootTable;
            if (asset == null)
            {
                Debug.LogWarning(name + " has no loot table, and its container type (" + _defId + ") has no default one, so it starts empty.", this);
                return new ItemNode { Id = ContainerId, DefId = _defId };
            }

            var table = asset.ToTable();
            foreach (var problem in LootRoller.Validate(catalog, table))
            {
                Debug.LogWarning("[loot] " + problem, asset);
            }

            var leftOut = new List<ItemNode>();
            var root = LootRoller.RollContainer(catalog, ContainerId, _defId, table, raidSeed, Uuid7.NewId, leftOut);
            if (leftOut.Count > 0)
            {
                Debug.LogWarning(name + " rolled " + leftOut.Count + " item(s) from " + asset.name + " that didn't fit, so they were left out. The table may be too generous for a " + _defId + ".", this);
            }

            return root;
        }

        private ItemNode BuildFixedContents(ItemCatalog catalog)
        {
            var root = new ItemNode { Id = ContainerId, DefId = _defId };
            foreach (var entry in _contents)
            {
                if (!catalog.TryGet(entry.DefId, out var definition))
                {
                    Debug.LogWarning(name + " lists unknown item '" + entry.DefId + "'.", this);
                    continue;
                }

                if (!GridPlacement.TryFindFreeSpot(catalog, root, definition, out var grid, out var x, out var y, out var rotated))
                {
                    Debug.LogWarning(name + " has no room left for " + entry.DefId + ".", this);
                    continue;
                }

                root.Children.Add(new ItemNode
                {
                    Id = Uuid7.NewId(),
                    DefId = entry.DefId,
                    Slot = grid,
                    X = x,
                    Y = y,
                    Rotated = rotated,
                    Quantity = Mathf.Clamp(entry.Quantity, 1, definition.MaxStack),
                    Durability = definition.HasDurability ? 1.0 : (double?)null,
                });
            }

            return root;
        }

        private void Awake()
        {
            ContainerId = StableId(gameObject.scene.path + ":" + HierarchyPath(transform));
            ContainerRegistry.Register(ContainerId, transform);
        }

        private void OnDestroy() => ContainerRegistry.Unregister(ContainerId, transform);

        private static string HierarchyPath(Transform t) => t.parent == null ? t.name : HierarchyPath(t.parent) + "/" + t.name;

        /// <summary>A UUID-shaped ID derived from a name (version 8), so it's the same on every machine.</summary>
        private static string StableId(string text)
        {
            byte[] hash;
            using (var sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            }

            hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            var id = new StringBuilder(36);
            for (int i = 0; i < 16; i++)
            {
                if (i == 4 || i == 6 || i == 8 || i == 10)
                {
                    id.Append('-');
                }

                id.Append(hash[i].ToString("x2"));
            }

            return id.ToString();
        }

        [Serializable]
        public sealed class LootEntry
        {
            public string DefId = "loot_gpu";
            public int Quantity = 1;
        }
    }
}
