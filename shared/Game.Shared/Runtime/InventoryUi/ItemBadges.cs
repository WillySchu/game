using System.Collections.Generic;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.InventoryUi
{
    /// <summary>
    /// The little bits of text and colour an item gets on the inventory screen, worked out from the catalog so
    /// every screen (raid, hideout, trader) shows them the same way.
    /// </summary>
    public static class ItemBadges
    {
        public const string Weapon = "weapon";
        public const string Magazine = "magazine";
        public const string Ammo = "ammo";
        public const string Gear = "gear";
        public const string Valuable = "valuable";
        public const string Barter = "barter";
        public const string Other = "other";

        /// <summary>
        /// Which colour family an item belongs to, from its tags: one of the constants above. The screen
        /// turns it into a USS class (inv-item--weapon and so on), so the colours live in the stylesheet.
        /// </summary>
        public static string Category(ItemDefinition def)
        {
            var tags = def?.Tags ?? new List<string>();
            foreach (var tag in tags)
            {
                if (tag.StartsWith("weapon_"))
                {
                    return Weapon;
                }

                if (tag.StartsWith("mag_"))
                {
                    return Magazine;
                }

                if (tag.StartsWith("ammo_"))
                {
                    return Ammo;
                }
            }

            foreach (var tag in tags)
            {
                switch (tag)
                {
                    case "armor":
                    case "rig":
                    case "backpack":
                        return Gear;
                    case "valuable":
                    case "currency":
                        return Valuable;
                    case "barter":
                        return Barter;
                }
            }

            return Other;
        }

        /// <summary>
        /// The number in the item's corner, or "" for none: a stack's size, a magazine's rounds out of what it
        /// holds ("12/30"), or the rounds in a weapon's magazine ("12/30", or "-" with no magazine in).
        /// </summary>
        public static string Count(ItemCatalog catalog, ItemNode node)
        {
            if (catalog == null || node == null || !catalog.TryGet(node.DefId, out var def))
            {
                return "";
            }

            if (def.MaxStack > 1)
            {
                return node.Quantity.ToString();
            }

            if (TryRounds(def, node, out var rounds))
            {
                return rounds;
            }

            var magazineSlot = FindSlot(def, SlotNames.Magazine);
            if (magazineSlot != null)
            {
                var magazine = Occupant(node, SlotNames.Magazine);
                return magazine != null && catalog.TryGet(magazine.DefId, out var magDef) && TryRounds(magDef, magazine, out var loaded)
                    ? loaded
                    : "-";
            }

            return "";
        }

        private static bool TryRounds(ItemDefinition def, ItemNode node, out string text)
        {
            var slot = FindSlot(def, SlotNames.Rounds);
            if (slot == null)
            {
                text = null;
                return false;
            }

            var rounds = Occupant(node, SlotNames.Rounds);
            var count = rounds?.Quantity ?? 0;
            text = slot.MaxQuantity > 0 ? count + "/" + slot.MaxQuantity : count.ToString();
            return true;
        }

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
