using System;
using System.Collections.Generic;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.Raid
{
    /// <summary>
    /// What AI carry: a character with their gear on it, built from item specs (<see cref="ItemSpecs"/>, the
    /// starter kit's format) with fresh IDs. For now just the target dummy's; the AI profiles' kits come next.
    /// </summary>
    public static class AIKits
    {
        /// <summary>
        /// The target dummy's: a loaded rifle, a light vest, and a rig with a spare magazine and a medkit. Enough to
        /// see a vest wear down, and to loot a body.
        /// </summary>
        public static readonly IReadOnlyList<ItemNode> TargetDummy = new[]
        {
            new ItemNode
            {
                DefId = "rifle_ak_basic", Slot = SlotNames.Primary, Durability = 1.0,
                Children = { Magazine() },
            },
            new ItemNode { DefId = "armor_vest_light", Slot = SlotNames.Armor, Durability = 1.0 },
            new ItemNode
            {
                DefId = "rig_chest_basic", Slot = SlotNames.Rig,
                Children = { Magazine(SlotNames.Grid), new ItemNode { DefId = "medkit_basic", Slot = SlotNames.Grid } },
            },
        };

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

        private static ItemNode Magazine(string slot = SlotNames.Magazine) => new ItemNode
        {
            DefId = "mag_ak_30",
            Slot = slot,
            Children = { new ItemNode { DefId = "ammo_545_ps", Slot = SlotNames.Rounds, Quantity = 30 } },
        };
    }
}
