using System.Collections.Generic;

namespace RaidZero.Shared.Loot
{
    /// <summary>
    /// What a container can hold: a number of picks, each a weighted choice among the entries (or nothing).
    /// Plain data for <see cref="LootRoller"/>; in Unity it's made from a LootTableAsset.
    /// </summary>
    public sealed class LootTable
    {
        /// <summary>For messages: the asset's name, say.</summary>
        public string Name { get; set; } = "";

        /// <summary>How many picks, chosen evenly between the two (inclusive).</summary>
        public int MinRolls { get; set; } = 1;

        public int MaxRolls { get; set; } = 1;

        /// <summary>The weight of a pick that adds nothing, against the entries' weights. 0 means every pick finds something.</summary>
        public int NothingWeight { get; set; }

        public List<LootEntry> Entries { get; set; } = new List<LootEntry>();
    }

    /// <summary>One choice in a <see cref="LootTable"/>: an item (with how many, what condition, how loaded), or another table.</summary>
    public sealed class LootEntry
    {
        /// <summary>How likely, against the table's other weights. 0 turns the entry off.</summary>
        public int Weight { get; set; } = 1;

        /// <summary>The item to make. Set this or <see cref="Table"/>, not both.</summary>
        public string DefId { get; set; }

        /// <summary>Another table to make one pick's worth from: all of its rolls.</summary>
        public LootTable Table { get; set; }

        /// <summary>For a stackable item, how many in the stack (inclusive). Leave both at 1 for anything else.</summary>
        public int MinQuantity { get; set; } = 1;

        public int MaxQuantity { get; set; } = 1;

        /// <summary>For an item with durability, its condition from 0 to 1.</summary>
        public double MinDurability { get; set; } = 1;

        public double MaxDurability { get; set; } = 1;

        /// <summary>
        /// For a weapon or a magazine, the chance (0 to 1) it comes loaded: a weapon with a magazine in it, and
        /// that magazine (or a magazine on its own) with rounds. The magazine and rounds are whatever the
        /// catalog has that fits the slots.
        /// </summary>
        public double LoadedChance { get; set; }

        /// <summary>When loaded, how full the magazine is, from 0 to 1 of what it holds.</summary>
        public double MinFill { get; set; } = 1;

        public double MaxFill { get; set; } = 1;
    }
}
