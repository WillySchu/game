using System.Collections.Generic;
using RaidZero.Items;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Loot;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// A place in a level where an item may be lying when the raid starts: a rifle on a workbench, a medkit
    /// on a shelf. The server rolls it at raid start with the raid's seed, like crates, and anything it gets
    /// lies here as a <see cref="GroundItem"/> that players pick up with F.
    ///
    /// Put it where the item should lie; the item is placed on the first surface below it. A table that
    /// rolls several items puts them side by side around the spot. Keep sibling spots' names unique: the
    /// roll for each spot comes from its place in the hierarchy, so a raid's seed repeats it exactly.
    /// </summary>
    public sealed class LooseLootSpot : MonoBehaviour
    {
        [Tooltip("What may lie here. Its picks decide how many items; a pick of nothing leaves the spot empty.")]
        [SerializeField]
        private LootTableAsset _lootTable = null;

        [Tooltip("The chance, from 0 to 1, that the spot is rolled at all. Below 1, some raids find nothing here.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float _chance = 1f;

        [Tooltip("For testing: this item every raid, instead of the table. Leave empty to use the table.")]
        [SerializeField]
        private string _fixedDefId = "";

        /// <summary>Where the spot's items lie: one per item rolled, spread around the spot.</summary>
        public Vector3 PlaceFor(int index, int count)
        {
            if (count <= 1)
            {
                return transform.position;
            }

            // A small ring, so two items rolled together don't sit inside each other.
            float angle = index * Mathf.PI * 2f / count;
            return transform.position + transform.rotation * new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 0.35f;
        }

        /// <summary>
        /// The items lying here this raid, fresh, with new IDs: none if the chance roll or the table says so.
        /// The same seed gives the same items.
        /// </summary>
        public List<ItemNode> Roll(ItemCatalog catalog, int raidSeed)
        {
            var items = new List<ItemNode>();
            var random = LootRandom.ForContainer(raidSeed, SpotKey());
            if (_chance < 1f && random.NextDouble() >= _chance)
            {
                return items;
            }

            if (!string.IsNullOrEmpty(_fixedDefId))
            {
                if (!catalog.TryGet(_fixedDefId, out var definition))
                {
                    Debug.LogWarning(name + " lists unknown item '" + _fixedDefId + "'.", this);
                    return items;
                }

                items.Add(new ItemNode
                {
                    Id = Uuid7.NewId(),
                    DefId = _fixedDefId,
                    Quantity = 1,
                    Durability = definition.HasDurability ? 1.0 : (double?)null,
                });
                return items;
            }

            if (_lootTable == null)
            {
                Debug.LogWarning(name + " has no loot table, so nothing lies there.", this);
                return items;
            }

            var table = _lootTable.ToTable();
            foreach (var problem in LootRoller.Validate(catalog, table))
            {
                Debug.LogWarning("[loot] " + problem, _lootTable);
            }

            items.AddRange(LootRoller.Roll(catalog, table, random, Uuid7.NewId));
            return items;
        }

        private string SpotKey() => gameObject.scene.path + ":" + HierarchyPath(transform);

        private static string HierarchyPath(Transform t) => t.parent == null ? t.name : HierarchyPath(t.parent) + "/" + t.name;

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.75f, 0.2f, 0.9f);
            Gizmos.DrawWireCube(transform.position + Vector3.up * 0.05f, new Vector3(0.3f, 0.1f, 0.3f));
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 0.4f);
        }
    }
}
