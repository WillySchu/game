using System;
using System.Collections.Generic;
using System.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.Loot
{
    /// <summary>
    /// Rolls loot tables into items and packs them into containers. The game server calls
    /// <see cref="RollContainer"/> for each crate at the start of a raid; the editor's simulator rolls the
    /// same way. Everything random comes from the <see cref="LootRandom"/> passed in, so a seed gives the
    /// same contents every time (the item IDs are new each time, which is what the inventory needs).
    /// </summary>
    public static class LootRoller
    {
        /// <summary>How deep tables may include tables. Deeper is refused by <see cref="Validate"/> and cut off when rolling.</summary>
        public const int MaxDepth = 8;

        /// <summary>
        /// A container for one raid: <paramref name="table"/> rolled from the raid's seed and this container's ID,
        /// and packed in, biggest items first. Whatever doesn't fit is left out and added to
        /// <paramref name="leftOut"/>, if given, so the caller can warn that the table is too generous.
        /// </summary>
        public static ItemNode RollContainer(ItemCatalog catalog, string containerId, string containerDefId, LootTable table,
            int raidSeed, Func<string> newId, List<ItemNode> leftOut = null)
        {
            var container = new ItemNode { Id = containerId, DefId = containerDefId };
            if (table != null)
            {
                Pack(catalog, container, Roll(catalog, table, LootRandom.ForContainer(raidSeed, containerId), newId), leftOut);
            }

            return container;
        }

        /// <summary>The items one roll of the table makes, not yet placed anywhere.</summary>
        public static List<ItemNode> Roll(ItemCatalog catalog, LootTable table, LootRandom random, Func<string> newId)
        {
            if (catalog == null || table == null || random == null || newId == null)
            {
                throw new ArgumentNullException(catalog == null ? nameof(catalog) : table == null ? nameof(table) : random == null ? nameof(random) : nameof(newId));
            }

            var items = new List<ItemNode>();
            RollInto(items, catalog, table, random, newId, 0);
            return items;
        }

        /// <summary>
        /// Puts the items in the container's grids, biggest first so small things fill the gaps, each in the
        /// first free spot (turned if that's the only way it fits). Returns how many went in.
        /// </summary>
        public static int Pack(ItemCatalog catalog, ItemNode container, IEnumerable<ItemNode> items, List<ItemNode> leftOut = null)
        {
            int packed = 0;
            var biggestFirst = items.OrderByDescending(i => catalog.TryGet(i.DefId, out var d) ? d.Width * d.Height : 0).ToList();
            foreach (var item in biggestFirst)
            {
                if (catalog.TryGet(item.DefId, out var def)
                    && GridPlacement.TryFindFreeSpot(catalog, container, def, out var grid, out var x, out var y, out var rotated))
                {
                    item.Slot = grid;
                    item.X = x;
                    item.Y = y;
                    item.Rotated = rotated;
                    container.Children.Add(item);
                    packed++;
                }
                else
                {
                    leftOut?.Add(item);
                }
            }

            return packed;
        }

        /// <summary>
        /// Everything wrong with the table, as sentences naming the table and entry: unknown items, impossible
        /// ranges, an entry with both or neither of an item and a table, tables that include themselves, and so on.
        /// Empty if it's fine.
        /// </summary>
        public static List<string> Validate(ItemCatalog catalog, LootTable table)
        {
            var errors = new List<string>();
            if (table == null)
            {
                errors.Add("There's no table.");
                return errors;
            }

            ValidateTable(catalog, table, new List<LootTable>(), new HashSet<LootTable>(), errors);
            return errors;
        }

        private static void RollInto(List<ItemNode> items, ItemCatalog catalog, LootTable table, LootRandom random, Func<string> newId, int depth)
        {
            if (depth > MaxDepth)
            {
                return;
            }

            int total = Math.Max(0, table.NothingWeight);
            foreach (var entry in table.Entries ?? new List<LootEntry>())
            {
                total += Math.Max(0, entry.Weight);
            }

            int rolls = random.Range(Math.Max(0, table.MinRolls), Math.Max(0, table.MaxRolls));
            for (int i = 0; i < rolls && total > 0; i++)
            {
                var entry = Pick(table, random.Next(total));
                if (entry == null)
                {
                    continue; // nothing
                }

                if (entry.Table != null)
                {
                    RollInto(items, catalog, entry.Table, random, newId, depth + 1);
                }
                else if (entry.DefId != null && catalog.TryGet(entry.DefId, out var def))
                {
                    items.Add(MakeItem(catalog, def, entry, random, newId));
                }
            }
        }

        private static LootEntry Pick(LootTable table, int roll)
        {
            roll -= Math.Max(0, table.NothingWeight);
            if (roll < 0)
            {
                return null;
            }

            foreach (var entry in table.Entries)
            {
                roll -= Math.Max(0, entry.Weight);
                if (roll < 0)
                {
                    return entry;
                }
            }

            return null;
        }

        private static ItemNode MakeItem(ItemCatalog catalog, ItemDefinition def, LootEntry entry, LootRandom random, Func<string> newId)
        {
            var item = new ItemNode { Id = newId(), DefId = def.DefId };
            if (def.MaxStack > 1)
            {
                item.Quantity = Clamp(random.Range(entry.MinQuantity, entry.MaxQuantity), 1, def.MaxStack);
            }

            if (def.HasDurability)
            {
                item.Durability = Math.Round(Clamp01(random.Range(entry.MinDurability, entry.MaxDurability)), 2);
            }

            if (entry.LoadedChance > 0 && random.NextDouble() < entry.LoadedChance)
            {
                double fill = Clamp01(random.Range(entry.MinFill, entry.MaxFill));
                Load(catalog, item, def, fill, random, newId);
            }

            return item;
        }

        // A weapon gets a magazine that fits, and a magazine (that one, or one rolled on its own) gets rounds.
        private static void Load(ItemCatalog catalog, ItemNode item, ItemDefinition def, double fill, LootRandom random, Func<string> newId)
        {
            var magazineSlot = FindSlot(def, SlotNames.Magazine);
            if (magazineSlot != null)
            {
                var magazineDef = Choose(catalog, magazineSlot, random);
                if (magazineDef != null)
                {
                    var magazine = new ItemNode { Id = newId(), DefId = magazineDef.DefId, Slot = SlotNames.Magazine };
                    item.Children.Add(magazine);
                    Load(catalog, magazine, magazineDef, fill, random, newId);
                }

                return;
            }

            var roundsSlot = FindSlot(def, SlotNames.Rounds);
            if (roundsSlot != null)
            {
                var roundsDef = Choose(catalog, roundsSlot, random);
                if (roundsDef != null)
                {
                    int capacity = roundsSlot.MaxQuantity > 0 ? roundsSlot.MaxQuantity : roundsDef.MaxStack;
                    int count = Math.Min((int)Math.Round(capacity * fill), roundsDef.MaxStack);
                    if (count > 0)
                    {
                        item.Children.Add(new ItemNode { Id = newId(), DefId = roundsDef.DefId, Slot = SlotNames.Rounds, Quantity = count });
                    }
                }
            }
        }

        // Something the catalog has that the slot takes, picked evenly; ordered by ID so a seed means the same thing.
        private static ItemDefinition Choose(ItemCatalog catalog, SlotSpec slot, LootRandom random)
        {
            var fits = catalog.All.Where(d => InventoryValidator.Accepts(slot, d)).OrderBy(d => d.DefId, StringComparer.Ordinal).ToList();
            return fits.Count == 0 ? null : fits[random.Next(fits.Count)];
        }

        private static SlotSpec FindSlot(ItemDefinition def, string name) => def.Slots?.FirstOrDefault(s => s.Name == name);

        private static void ValidateTable(ItemCatalog catalog, LootTable table, List<LootTable> path, HashSet<LootTable> done, List<string> errors)
        {
            if (path.Contains(table))
            {
                errors.Add(Describe(path) + " includes " + Name(table) + ", which includes it again: tables can't include themselves.");
                return;
            }

            if (path.Count >= MaxDepth)
            {
                errors.Add(Describe(path) + ": tables go more than " + MaxDepth + " deep.");
                return;
            }

            if (!done.Add(table))
            {
                return; // checked already, through another path
            }

            path.Add(table);
            var where = Describe(path);
            if (table.MinRolls < 0 || table.MaxRolls < table.MinRolls)
            {
                errors.Add(where + ": rolls go from " + table.MinRolls + " to " + table.MaxRolls + "; the minimum must be 0 or more and no more than the maximum.");
            }

            if (table.NothingWeight < 0)
            {
                errors.Add(where + ": the nothing weight can't be negative.");
            }

            var entries = table.Entries ?? new List<LootEntry>();
            if (table.MaxRolls > 0 && table.NothingWeight <= 0 && !entries.Any(e => e.Weight > 0))
            {
                errors.Add(where + ": it rolls, but has no entries with any weight.");
            }

            for (int i = 0; i < entries.Count; i++)
            {
                ValidateEntry(catalog, entries[i], where + ", entry " + (i + 1), path, done, errors);
            }

            path.RemoveAt(path.Count - 1);
        }

        private static void ValidateEntry(ItemCatalog catalog, LootEntry entry, string where, List<LootTable> path, HashSet<LootTable> done, List<string> errors)
        {
            if (entry == null)
            {
                errors.Add(where + " is empty.");
                return;
            }

            if (entry.Weight < 0)
            {
                errors.Add(where + ": the weight can't be negative.");
            }

            bool hasItem = !string.IsNullOrEmpty(entry.DefId);
            if (hasItem == (entry.Table != null))
            {
                errors.Add(where + (hasItem ? ": set an item or a table, not both." : ": set an item or a table."));
                return;
            }

            if (entry.Table != null)
            {
                ValidateTable(catalog, entry.Table, path, done, errors);
                return;
            }

            if (catalog == null || !catalog.TryGet(entry.DefId, out var def))
            {
                errors.Add(where + ": there's no item '" + entry.DefId + "' in the catalog.");
                return;
            }

            if (entry.MinQuantity < 1 || entry.MaxQuantity < entry.MinQuantity)
            {
                errors.Add(where + " (" + def.DefId + "): the quantity goes from " + entry.MinQuantity + " to " + entry.MaxQuantity + "; it must start at 1 or more and not go down.");
            }
            else if (entry.MaxQuantity > Math.Max(1, def.MaxStack))
            {
                errors.Add(where + " (" + def.DefId + "): up to " + entry.MaxQuantity + ", but " + (def.MaxStack > 1 ? "a stack holds " + def.MaxStack : "it doesn't stack") + ".");
            }

            if (!InUnit(entry.MinDurability) || !InUnit(entry.MaxDurability) || entry.MaxDurability < entry.MinDurability)
            {
                errors.Add(where + " (" + def.DefId + "): durability goes from 0 to 1, minimum first.");
            }

            if (!InUnit(entry.LoadedChance))
            {
                errors.Add(where + " (" + def.DefId + "): the loaded chance goes from 0 to 1.");
            }
            else if (entry.LoadedChance > 0)
            {
                if (FindSlot(def, SlotNames.Magazine) == null && FindSlot(def, SlotNames.Rounds) == null)
                {
                    errors.Add(where + " (" + def.DefId + "): it has a loaded chance, but nothing to load.");
                }

                if (!InUnit(entry.MinFill) || !InUnit(entry.MaxFill) || entry.MaxFill < entry.MinFill)
                {
                    errors.Add(where + " (" + def.DefId + "): fill goes from 0 to 1, minimum first.");
                }
            }
        }

        private static string Describe(List<LootTable> path) => string.Join(" > ", path.Select(Name));

        private static string Name(LootTable table) => string.IsNullOrEmpty(table.Name) ? "(unnamed table)" : table.Name;

        private static bool InUnit(double value) => value >= 0 && value <= 1;

        private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

        private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
    }
}
