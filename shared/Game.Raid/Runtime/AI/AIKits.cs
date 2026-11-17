using System;
using System.Collections.Generic;
using System.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Loot;

namespace RaidZero.Raid
{
    /// <summary>
    /// One way an AI can be kitted out: a gun with a loaded magazine, armor, a rig and a backpack, spare
    /// magazines, and a few fixed items. The AI's profile names one or more, picked by <see cref="Weight"/>, and
    /// may add a loot table's extras. All by def ID; empty for none.
    /// </summary>
    public sealed class AIKitSpec
    {
        public string Name { get; set; } = "";

        /// <summary>The gun: in the primary slot, or the secondary for a handgun (whichever takes it).</summary>
        public string Weapon { get; set; }

        /// <summary>The magazine loaded in it, and the type the spares are.</summary>
        public string Magazine { get; set; }

        /// <summary>What fills every magazine, as full as it goes.</summary>
        public string Ammo { get; set; }

        /// <summary>How many more magazines, full, in the rig and then the backpack.</summary>
        public int SpareMagazines { get; set; }

        public string Armor { get; set; }
        public string Rig { get; set; }
        public string Backpack { get; set; }

        /// <summary>Anything else it always carries (a medkit, say), in the rig and then the backpack.</summary>
        public List<string> Items { get; set; } = new List<string>();

        /// <summary>How likely this kit is against the profile's others.</summary>
        public int Weight { get; set; } = 1;
    }

    /// <summary>
    /// What AI carry: a character with their gear on it, with fresh IDs, ready for the raid inventory. From a
    /// kit (<see cref="AIKitSpec"/>, with a loot table's extras), or from item specs (<see cref="ItemSpecs"/>,
    /// the starter kit's format).
    /// </summary>
    public static class AIKits
    {
        /// <summary>
        /// The target dummy's: a loaded rifle, a light vest, and a rig with a spare magazine and a medkit. Enough to
        /// see a vest wear down, and to loot a body.
        /// </summary>
        public static readonly AIKitSpec TargetDummy = new AIKitSpec
        {
            Name = "Target dummy",
            Weapon = "rifle_ak_basic",
            Magazine = "mag_ak_30",
            Ammo = "ammo_545_ps",
            SpareMagazines = 1,
            Armor = "armor_vest_light",
            Rig = "rig_chest_basic",
            Items = { "medkit_basic" },
        };

        /// <summary>
        /// A character wearing the kit: the gun loaded in its primary slot, armor, rig and backpack in theirs, and
        /// the spare magazines, fixed items and <paramref name="extras"/> (rolled with <paramref name="random"/>)
        /// packed into the rig, then the backpack. Whatever doesn't fit is left out, into <paramref name="leftOut"/>
        /// if given, so a caller can warn that a kit carries more than it has room for. Items with durability are
        /// new.
        /// </summary>
        /// <exception cref="ItemSpecException">An item the catalog doesn't have, or gear that doesn't go together
        /// (a magazine the gun doesn't take, say).</exception>
        public static ItemNode Build(ItemCatalog catalog, AIKitSpec kit, LootTable extras, LootRandom random, Func<string> newId,
            List<ItemNode> leftOut = null)
        {
            if (catalog == null || kit == null || newId == null)
            {
                throw new ArgumentNullException(catalog == null ? nameof(catalog) : kit == null ? nameof(kit) : nameof(newId));
            }

            var worn = new List<ItemNode>();
            if (!string.IsNullOrEmpty(kit.Weapon))
            {
                var gun = Spec(catalog, kit.Weapon, WeaponSlotFor(catalog, kit.Weapon));
                if (!string.IsNullOrEmpty(kit.Magazine))
                {
                    gun.Children.Add(Magazine(catalog, kit, SlotNames.Magazine));
                }

                worn.Add(gun);
            }

            foreach (var (defId, slot) in new[] { (kit.Armor, SlotNames.Armor), (kit.Rig, SlotNames.Rig), (kit.Backpack, SlotNames.Backpack) })
            {
                if (!string.IsNullOrEmpty(defId))
                {
                    worn.Add(Spec(catalog, defId, slot));
                }
            }

            var character = Build(catalog, worn, newId);

            // Then what it carries: spares, fixed items and extras, in the rig and then the backpack.
            var carried = new List<ItemNode>();
            for (int i = 0; i < kit.SpareMagazines && !string.IsNullOrEmpty(kit.Magazine); i++)
            {
                carried.Add(ItemSpecs.Build(catalog, Magazine(catalog, kit, SlotNames.Grid), newId));
            }

            foreach (var defId in kit.Items ?? new List<string>())
            {
                carried.Add(ItemSpecs.Build(catalog, Spec(catalog, defId, SlotNames.Grid), newId));
            }

            if (extras != null && random != null)
            {
                carried.AddRange(LootRoller.Roll(catalog, extras, random, newId));
            }

            var left = carried;
            foreach (var carrier in new[] { SlotNames.Rig, SlotNames.Backpack })
            {
                var container = character.ChildInSlot(carrier);
                if (container != null && left.Count > 0)
                {
                    var stillLeft = new List<ItemNode>();
                    LootRoller.Pack(catalog, container, left, stillLeft);
                    left = stillLeft;
                }
            }

            leftOut?.AddRange(left);

            var problems = InventoryValidator.Validate(catalog, character);
            if (problems.Count > 0)
            {
                throw new ItemSpecException("kit '" + kit.Name + "' doesn't go together: " + string.Join("; ", problems.Select(p => p.Message)));
            }

            return character;
        }

        /// <summary>A character carrying the specs, with fresh IDs, ready for the raid inventory.</summary>
        /// <exception cref="ItemSpecException">An item the catalog doesn't have, or no room for one.</exception>
        public static ItemNode Build(ItemCatalog catalog, IEnumerable<ItemNode> specs, Func<string> newId)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            var character = new ItemNode { Id = newId(), DefId = WellKnownDefs.Character };
            ItemSpecs.AddChildren(catalog, character, specs, newId);
            return character;
        }

        /// <summary>The character slot that holds the gun the AI fights with: primary if it has one there, else secondary; null with neither.</summary>
        public static string WeaponSlotOf(ItemNode character) =>
            character?.ChildInSlot(SlotNames.Primary) != null ? SlotNames.Primary
            : character?.ChildInSlot(SlotNames.Secondary) != null ? SlotNames.Secondary
            : null;

        // Primary, unless only the secondary slot takes it (a handgun).
        private static string WeaponSlotFor(ItemCatalog catalog, string weaponDefId)
        {
            if (catalog.TryGet(WellKnownDefs.Character, out var character) && catalog.TryGet(weaponDefId, out var weapon) && weapon.Tags != null)
            {
                foreach (var slot in new[] { SlotNames.Primary, SlotNames.Secondary })
                {
                    var spec = character.Slots?.Find(s => s.Name == slot);
                    if (spec?.Accepts != null && weapon.Tags.Any(spec.Accepts.Contains))
                    {
                        return slot;
                    }
                }
            }

            return SlotNames.Primary;
        }

        // A full magazine of the kit's type, as a spec.
        private static ItemNode Magazine(ItemCatalog catalog, AIKitSpec kit, string slot)
        {
            var magazine = Spec(catalog, kit.Magazine, slot);
            if (!string.IsNullOrEmpty(kit.Ammo) && catalog.TryGet(kit.Magazine, out var magDef) && catalog.TryGet(kit.Ammo, out var ammoDef))
            {
                var rounds = magDef.Slots?.Find(s => s.Name == SlotNames.Rounds);
                var full = rounds != null && rounds.MaxQuantity > 0 ? Math.Min(rounds.MaxQuantity, Math.Max(1, ammoDef.MaxStack)) : Math.Max(1, ammoDef.MaxStack);
                magazine.Children.Add(new ItemNode { DefId = kit.Ammo, Slot = SlotNames.Rounds, Quantity = full });
            }

            return magazine;
        }

        // One item as a spec, new if it wears.
        private static ItemNode Spec(ItemCatalog catalog, string defId, string slot)
        {
            if (!catalog.TryGet(defId, out var def))
            {
                throw new ItemSpecException("unknown def_id '" + defId + "'.");
            }

            return new ItemNode { DefId = defId, Slot = slot, Durability = def.HasDurability ? 1.0 : (double?)null };
        }
    }
}
