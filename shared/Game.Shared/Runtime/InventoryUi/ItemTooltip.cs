using System.Collections.Generic;
using System.Globalization;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.InventoryUi
{
    /// <summary>One "label: value" row of a tooltip, such as Condition / 82%.</summary>
    public sealed class TooltipLine
    {
        public TooltipLine(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }

        public string Value { get; }

        public override string ToString() => Label + ": " + Value;
    }

    /// <summary>What an item's tooltip says: a title, a category under it, rows of facts, and an optional description.</summary>
    public sealed class ItemTooltipContent
    {
        public ItemTooltipContent(string title, string category, IReadOnlyList<TooltipLine> lines, string description)
        {
            Title = title;
            Category = category;
            Lines = lines;
            Description = description;
        }

        public string Title { get; }

        public string Category { get; }

        public IReadOnlyList<TooltipLine> Lines { get; }

        /// <summary>Plain facts about the item, from its asset; null when it has none.</summary>
        public string Description { get; }
    }

    /// <summary>
    /// What the inventory screen's tooltip shows for an item, from the catalog and the item itself. Only facts the
    /// data has, and each only where it applies: condition for items that wear, the stack for stackables, what's
    /// loaded in weapons and magazines, and how full a container is.
    /// </summary>
    public static class ItemTooltip
    {
        public static ItemTooltipContent Build(ItemCatalog catalog, ItemNode node, string description = null)
        {
            if (node == null)
            {
                return null;
            }

            ItemDefinition def = null;
            if (catalog == null || !catalog.TryGet(node.DefId, out def))
            {
                return new ItemTooltipContent(node.DefId, "Unknown item", new List<TooltipLine>(), null);
            }

            var lines = new List<TooltipLine>();
            if (def.HasDurability && node.Durability.HasValue)
            {
                lines.Add(new TooltipLine("Condition", Percent(node.Durability.Value)));
            }

            if (def.MaxStack > 1)
            {
                lines.Add(new TooltipLine("Stack", node.Quantity + " / " + def.MaxStack));
            }

            if (FindSlot(def, SlotNames.Magazine) != null)
            {
                var magazine = Occupant(node, SlotNames.Magazine);
                if (magazine == null || !catalog.TryGet(magazine.DefId, out var magazineDef))
                {
                    lines.Add(new TooltipLine("Magazine", "None"));
                }
                else
                {
                    lines.Add(new TooltipLine("Magazine", NameOf(magazineDef)));
                    AddRounds(catalog, magazineDef, magazine, lines);
                }
            }

            AddRounds(catalog, def, node, lines);

            if (def.HasGrids())
            {
                lines.Add(new TooltipLine("Contents", Contents(catalog, def, node)));
            }

            return new ItemTooltipContent(NameOf(def), CategoryName(def), lines, string.IsNullOrWhiteSpace(description) ? null : description.Trim());
        }

        /// <summary>
        /// The words under the item's name, from its tags: a little more specific than the colour family
        /// (<see cref="ItemBadges.Category"/>), so a rig and a backpack read differently though they share a colour.
        /// </summary>
        public static string CategoryName(ItemDefinition def)
        {
            var tags = def?.Tags ?? new List<string>();
            foreach (var tag in tags)
            {
                switch (tag)
                {
                    case "weapon_long":
                        return "Long gun";
                    case "weapon_short":
                        return "Handgun";
                    case "armor":
                        return "Body armor";
                    case "rig":
                        return "Chest rig";
                    case "backpack":
                        return "Backpack";
                    case "currency":
                        return "Currency";
                    case "valuable":
                        return "Valuable";
                    case "barter":
                        return "Barter item";
                }
            }

            switch (ItemBadges.Category(def))
            {
                case ItemBadges.Weapon:
                    return "Weapon";
                case ItemBadges.Magazine:
                    return "Magazine";
                case ItemBadges.Ammo:
                    return "Ammunition";
                default:
                    return "Item";
            }
        }

        // "24 / 30" for a magazine's rounds, with the cartridge's name when there are any.
        private static void AddRounds(ItemCatalog catalog, ItemDefinition def, ItemNode node, List<TooltipLine> lines)
        {
            var slot = FindSlot(def, SlotNames.Rounds);
            if (slot == null)
            {
                return;
            }

            var rounds = Occupant(node, SlotNames.Rounds);
            var count = rounds?.Quantity ?? 0;
            var text = slot.MaxQuantity > 0 ? count + " / " + slot.MaxQuantity : count.ToString(CultureInfo.InvariantCulture);
            if (rounds != null && catalog.TryGet(rounds.DefId, out var roundsDef))
            {
                text += " (" + NameOf(roundsDef) + ")";
            }

            lines.Add(new TooltipLine("Rounds", text));
        }

        // "6 items, 14 / 25 cells", counting only what sits directly in the container's grids.
        private static string Contents(ItemCatalog catalog, ItemDefinition def, ItemNode node)
        {
            int total = 0;
            var gridNames = new HashSet<string>();
            foreach (var grid in def.Grids)
            {
                total += grid.Width * grid.Height;
                gridNames.Add(grid.Name);
            }

            int items = 0;
            int used = 0;
            foreach (var child in node.Children ?? new List<ItemNode>())
            {
                if (child.Slot == null || !gridNames.Contains(child.Slot))
                {
                    continue;
                }

                items++;
                if (catalog.TryGet(child.DefId, out var childDef))
                {
                    GridPlacement.GetSize(childDef, child.Rotated, out int w, out int h);
                    used += w * h;
                }
            }

            var cells = used + " / " + total + " cells";
            return items == 0 ? "Empty, " + cells : items + (items == 1 ? " item, " : " items, ") + cells;
        }

        private static string Percent(double fraction)
        {
            var clamped = fraction < 0 ? 0 : fraction > 1 ? 1 : fraction;
            return ((int)System.Math.Round(clamped * 100, System.MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "%";
        }

        private static string NameOf(ItemDefinition def) => string.IsNullOrEmpty(def.DisplayName) ? def.DefId : def.DisplayName;

        private static SlotSpec FindSlot(ItemDefinition def, string name)
        {
            if (def.Slots == null)
            {
                return null;
            }

            foreach (var slot in def.Slots)
            {
                if (slot.Name == name)
                {
                    return slot;
                }
            }

            return null;
        }

        private static ItemNode Occupant(ItemNode parent, string slot)
        {
            if (parent.Children == null)
            {
                return null;
            }

            foreach (var child in parent.Children)
            {
                if (child.Slot == slot)
                {
                    return child;
                }
            }

            return null;
        }
    }
}
