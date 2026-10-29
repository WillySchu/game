using System;
using System.Collections.Generic;
using RaidZero.Shared.Loot;
using UnityEngine;

namespace RaidZero.Items
{
    /// <summary>
    /// What a container can hold. Each pick is a weighted choice among the entries, or nothing; an entry is an
    /// item (how many, what condition, whether it's loaded) or another table, so "medical" can be part of
    /// "office desk". A container type's item asset names its default table, and a crate in a scene can use
    /// its own instead. The game server rolls them at the start of each raid from the raid's seed.
    /// The inspector checks the table and can simulate it.
    /// </summary>
    [CreateAssetMenu(menuName = "Raid Zero/Loot Table", fileName = "new_loot_table")]
    public sealed class LootTableAsset : ScriptableObject
    {
        [Tooltip("How many picks, chosen evenly between the two (inclusive).")]
        [Min(0)]
        [SerializeField]
        private int _minRolls = 1;

        [Min(0)]
        [SerializeField]
        private int _maxRolls = 1;

        [Tooltip("The weight of a pick that finds nothing, against the entries' weights. 0: every pick finds something.")]
        [Min(0)]
        [SerializeField]
        private int _nothingWeight = 0;

        [SerializeField]
        private List<Entry> _entries = new List<Entry>();

        public IReadOnlyList<Entry> Entries => _entries;

        /// <summary>
        /// The table as the shared roller sees it, with the tables it includes. A table that includes itself
        /// becomes a loop the roller stops after <see cref="LootRoller.MaxDepth"/> levels, and that
        /// <see cref="LootRoller.Validate"/> reports.
        /// </summary>
        public LootTable ToTable() => ToTable(new Dictionary<LootTableAsset, LootTable>());

        private LootTable ToTable(Dictionary<LootTableAsset, LootTable> made)
        {
            if (made.TryGetValue(this, out var existing))
            {
                return existing;
            }

            var table = new LootTable { Name = name, MinRolls = _minRolls, MaxRolls = _maxRolls, NothingWeight = _nothingWeight };
            made[this] = table;
            foreach (var entry in _entries)
            {
                if (entry == null)
                {
                    continue;
                }

                table.Entries.Add(new LootEntry
                {
                    Weight = entry.Weight,
                    DefId = entry.Item != null ? entry.Item.DefId : null,
                    Table = entry.Table != null ? entry.Table.ToTable(made) : null,
                    MinQuantity = entry.MinQuantity,
                    MaxQuantity = entry.MaxQuantity,
                    MinDurability = entry.MinCondition,
                    MaxDurability = entry.MaxCondition,
                    LoadedChance = entry.LoadedChance,
                    MinFill = entry.MinFill,
                    MaxFill = entry.MaxFill,
                });
            }

            return table;
        }

        /// <summary>
        /// One choice. New entries get the defaults below when added with the inspector's + button
        /// (LootTableAssetEditor sets them; Unity alone would make them all zero).
        /// </summary>
        [Serializable]
        public sealed class Entry
        {
            [Tooltip("How likely, against the table's other weights. 0 turns the entry off.")]
            [Min(0)]
            public int Weight = 1;

            [Tooltip("The item. Set this or Table, not both.")]
            public ItemDefinitionAsset Item;

            [Tooltip("Another table: one pick here rolls all of that table's picks.")]
            public LootTableAsset Table;

            [Tooltip("For a stackable item, how many in the stack. Leave at 1 for anything else.")]
            [Min(1)]
            public int MinQuantity = 1;

            [Min(1)]
            public int MaxQuantity = 1;

            [Tooltip("For an item with durability, its condition: 1 is new.")]
            [Range(0, 1)]
            public float MinCondition = 1;

            [Range(0, 1)]
            public float MaxCondition = 1;

            [Tooltip("For a weapon or magazine, the chance it comes loaded: a weapon with a magazine, a magazine with rounds.")]
            [Range(0, 1)]
            public float LoadedChance;

            [Tooltip("When loaded, how full the magazine is.")]
            [Range(0, 1)]
            public float MinFill = 1;

            [Range(0, 1)]
            public float MaxFill = 1;
        }
    }
}
