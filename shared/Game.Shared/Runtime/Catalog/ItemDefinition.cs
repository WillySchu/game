using System.Collections.Generic;

namespace RaidZero.Shared.Catalog
{
    /// <summary>
    /// One kind of item, as exported from the Unity ItemDefinition ScriptableObjects into
    /// item_catalog.json. Serialized with a snake_case naming policy (def_id, max_stack, ...).
    /// </summary>
    public sealed class ItemDefinition
    {
        /// <summary>Stable ID such as "rifle_ak_basic". Never rename once an item has existed in a stash.</summary>
        public string DefId { get; set; }

        /// <summary>Cosmetic. Not part of the catalog version hash, so renaming it needs no redeploy.</summary>
        public string DisplayName { get; set; }

        /// <summary>Footprint in grid cells when not rotated.</summary>
        public int Width { get; set; } = 1;

        public int Height { get; set; } = 1;

        /// <summary>1 for non-stackable items.</summary>
        public int MaxStack { get; set; } = 1;

        /// <summary>Durability is stored per instance as 0..1. Only non-stackable items can have it.</summary>
        public bool HasDurability { get; set; }

        /// <summary>Tags decide which named slots accept this item, e.g. "weapon_long" or "ammo_545".</summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// The grids this item holds other items in: the stash, a backpack's compartment, each of a rig's pockets.
        /// Each is separate, so an item can't straddle two. Items in a grid have the grid's name as their slot.
        /// A container with one grid names it "grid" (<see cref="GridSpec.MainName"/>). Order matters: "the first
        /// free spot" fills the grids in this order.
        /// </summary>
        public List<GridSpec> Grids { get; set; } = new List<GridSpec>();

        /// <summary>Named slots this item has (a rifle's magazine, a magazine's rounds, the character's primary).</summary>
        public List<SlotSpec> Slots { get; set; } = new List<SlotSpec>();

        /// <summary>The grid with this name, or null if the item has none by that name (it's a slot, or nothing).</summary>
        public GridSpec FindGrid(string name)
        {
            if (name == null || Grids == null)
            {
                return null;
            }

            foreach (var grid in Grids)
            {
                if (grid != null && grid.Name == name)
                {
                    return grid;
                }
            }

            return null;
        }

        /// <summary>Whether the item holds other items in at least one grid.</summary>
        public bool HasGrids() => Grids != null && Grids.Count > 0;
    }

    /// <summary>One grid of cells inside an item: a whole compartment, or one pocket of several.</summary>
    public sealed class GridSpec
    {
        /// <summary>The name of a container's only grid, by convention. Also what "any grid" targets use.</summary>
        public const string MainName = "grid";

        /// <summary>Unique within the item, and never the same as one of its named slots.</summary>
        public string Name { get; set; } = MainName;

        public int Width { get; set; }

        public int Height { get; set; }

        /// <summary>A grid named "grid": what a container with a single compartment has.</summary>
        public static GridSpec Main(int width, int height) => new GridSpec { Name = MainName, Width = width, Height = height };
    }

    public sealed class SlotSpec
    {
        public string Name { get; set; }

        /// <summary>The slot accepts an item carrying any one of these tags.</summary>
        public List<string> Accepts { get; set; } = new List<string>();

        /// <summary>
        /// Upper bound on the quantity of the stack in this slot, on top of the item's own max stack.
        /// A 30-round magazine's "rounds" slot has 30 even though loose ammo stacks to 60. 0 means no extra limit.
        /// </summary>
        public int MaxQuantity { get; set; }
    }

    /// <summary>The whole item_catalog.json file.</summary>
    public sealed class CatalogDocument
    {
        /// <summary>Hash of the rule-relevant fields, written by the exporter. See CatalogHash.</summary>
        public string CatalogVersion { get; set; }

        public List<ItemDefinition> Items { get; set; } = new List<ItemDefinition>();
    }

    /// <summary>Definitions the backend and server rely on existing in every catalog.</summary>
    public static class WellKnownDefs
    {
        /// <summary>Root container of a player's stash.</summary>
        public const string Stash = "stash";

        /// <summary>Root container of a player's equipped gear; checked out whole into a raid.</summary>
        public const string Character = "character";

        /// <summary>
        /// A pile on the ground in a raid, holding one dropped item. Its grid only needs to be big enough for
        /// the largest item. Raid servers need it; the backend never sees one.
        /// </summary>
        public const string Ground = "ground";
    }
}
