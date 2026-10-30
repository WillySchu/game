using System;
using System.Collections.Generic;
using System.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.Loot
{
    /// <summary>How often one item turned up in a <see cref="LootSimulation"/>.</summary>
    public sealed class LootOdds
    {
        public LootOdds(string defId, double chance, double averageCount, double averageQuantity)
        {
            DefId = defId;
            Chance = chance;
            AverageCount = averageCount;
            AverageQuantity = averageQuantity;
        }

        public string DefId { get; }

        /// <summary>The share of containers (0 to 1) with at least one.</summary>
        public double Chance { get; }

        /// <summary>How many of it a container gets on average, counting the ones with none.</summary>
        public double AverageCount { get; }

        /// <summary>For a stack, the average stack size when it turns up.</summary>
        public double AverageQuantity { get; }
    }

    /// <summary>What a table gives over many containers, for tuning it without playing.</summary>
    public sealed class LootSimulation
    {
        private LootSimulation(int runs, double averageItems, double emptyChance, double averageLeftOut, IReadOnlyList<LootOdds> items)
        {
            Runs = runs;
            AverageItems = averageItems;
            EmptyChance = emptyChance;
            AverageLeftOut = averageLeftOut;
            Items = items;
        }

        public int Runs { get; }

        /// <summary>Items per container on average (a loaded weapon counts once).</summary>
        public double AverageItems { get; }

        /// <summary>The share of containers that came up empty.</summary>
        public double EmptyChance { get; }

        /// <summary>Items per container that didn't fit, on average. Above 0 means the table is too much for the container.</summary>
        public double AverageLeftOut { get; }

        /// <summary>Every item that turned up, most likely first.</summary>
        public IReadOnlyList<LootOdds> Items { get; }

        /// <summary>
        /// Rolls the table <paramref name="runs"/> times, packing each roll into a fresh container of
        /// <paramref name="containerDefId"/> if given (otherwise nothing is left out).
        /// </summary>
        public static LootSimulation Run(ItemCatalog catalog, LootTable table, string containerDefId, int runs, ulong seed = 1)
        {
            if (runs < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(runs), "Run it at least once.");
            }

            var random = new LootRandom(seed);
            int nextId = 0;
            Func<string> newId = () => "sim-" + nextId++;
            var containing = new Dictionary<string, int>();
            var counts = new Dictionary<string, int>();
            var quantities = new Dictionary<string, long>();
            long items = 0, leftOutTotal = 0;
            int empty = 0;

            for (int run = 0; run < runs; run++)
            {
                var rolled = LootRoller.Roll(catalog, table, random, newId);
                var kept = rolled;
                if (containerDefId != null)
                {
                    var leftOut = new List<ItemNode>();
                    var container = new ItemNode { Id = newId(), DefId = containerDefId };
                    LootRoller.Pack(catalog, container, rolled, leftOut);
                    leftOutTotal += leftOut.Count;
                    kept = container.Children;
                }

                items += kept.Count;
                if (kept.Count == 0)
                {
                    empty++;
                }

                foreach (var item in kept)
                {
                    counts[item.DefId] = (counts.TryGetValue(item.DefId, out var c) ? c : 0) + 1;
                    quantities[item.DefId] = (quantities.TryGetValue(item.DefId, out var q) ? q : 0) + item.Quantity;
                }

                foreach (var defId in kept.Select(i => i.DefId).Distinct())
                {
                    containing[defId] = (containing.TryGetValue(defId, out var n) ? n : 0) + 1;
                }
            }

            var odds = containing.Keys
                .Select(id => new LootOdds(id, (double)containing[id] / runs, (double)counts[id] / runs, (double)quantities[id] / counts[id]))
                .OrderByDescending(o => o.Chance)
                .ThenBy(o => o.DefId, StringComparer.Ordinal)
                .ToList();
            return new LootSimulation(runs, (double)items / runs, (double)empty / runs, (double)leftOutTotal / runs, odds);
        }
    }
}
