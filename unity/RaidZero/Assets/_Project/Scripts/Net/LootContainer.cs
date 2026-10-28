using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
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
    /// The fixed contents are a placeholder for loot tables: a weighted table rolled from the raid's seed
    /// replaces the list later, behind the same <see cref="BuildContents"/>.
    /// </summary>
    public sealed class LootContainer : MonoBehaviour
    {
        [Tooltip("The container's item definition, which sets its grid size.")]
        [SerializeField]
        private string _defId = "crate_wooden";

        [Tooltip("What the server puts in it at the start of each raid.")]
        [SerializeField]
        private List<LootEntry> _contents = new List<LootEntry>();

        public string ContainerId { get; private set; }

        /// <summary>The container and fresh items for one raid. Items that don't fit are left out, with a warning.</summary>
        public ItemNode BuildContents(ItemCatalog catalog)
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
