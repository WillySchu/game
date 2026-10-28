using System;
using RaidZero.Shared.Catalog;

namespace RaidZero.Shared.Inventory
{
    /// <summary>
    /// Grid fit checks. The client uses these to preview where a dragged item can go; the game
    /// server and backend use the same code to decide, so the preview and the result always agree.
    ///
    /// A container can have several grids (a rig's pockets). Each is checked on its own: an item sits in
    /// exactly one, named by its slot, and can't straddle two.
    /// </summary>
    public static class GridPlacement
    {
        /// <summary>The item's footprint, with width and height swapped when rotated.</summary>
        public static void GetSize(ItemDefinition definition, bool rotated, out int width, out int height)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            width = rotated ? definition.Height : definition.Width;
            height = rotated ? definition.Width : definition.Height;
        }

        /// <summary>
        /// True when an item of this definition fits at (x, y) in the container's grid named
        /// <paramref name="gridName"/> without leaving it or overlapping another item in it.
        /// <paramref name="ignoreItemId"/> skips one child, so an item can be checked against a new position
        /// in the grid it already occupies. False if the container has no grid by that name.
        /// </summary>
        public static bool Fits(ItemCatalog catalog, ItemNode container, string gridName, ItemDefinition definition, int x, int y, bool rotated, string ignoreItemId = null)
        {
            var occupied = BuildOccupancy(catalog, container, gridName, ignoreItemId, out var grid);
            if (occupied == null)
            {
                return false;
            }

            GetSize(definition, rotated, out int w, out int h);
            return IsFree(occupied, grid, x, y, w, h);
        }

        /// <summary>
        /// Finds the first free position in any of the container's grids, in the order the definition lists
        /// them. Used for overflow, such as loot that doesn't fit where it was, and for "anywhere it fits".
        /// </summary>
        public static bool TryFindFreeSpot(ItemCatalog catalog, ItemNode container, ItemDefinition definition, out string gridName, out int x, out int y, out bool rotated)
        {
            gridName = null;
            x = 0;
            y = 0;
            rotated = false;
            if (catalog == null || container == null || !catalog.TryGet(container.DefId, out var containerDef) || containerDef.Grids == null)
            {
                return false;
            }

            foreach (var grid in containerDef.Grids)
            {
                if (grid != null && TryFindFreeSpot(catalog, container, grid.Name, definition, out x, out y, out rotated))
                {
                    gridName = grid.Name;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the first free position in one grid, scanning rows top to bottom and columns left to right,
        /// trying the item unrotated before rotated.
        /// </summary>
        public static bool TryFindFreeSpot(ItemCatalog catalog, ItemNode container, string gridName, ItemDefinition definition, out int x, out int y, out bool rotated)
        {
            x = 0;
            y = 0;
            rotated = false;

            var occupied = BuildOccupancy(catalog, container, gridName, null, out var grid);
            if (occupied == null)
            {
                return false;
            }

            for (int r = 0; r < 2; r++)
            {
                bool rot = r == 1;
                if (rot && definition.Width == definition.Height)
                {
                    break;
                }

                GetSize(definition, rot, out int w, out int h);
                for (int row = 0; row + h <= grid.Height; row++)
                {
                    for (int col = 0; col + w <= grid.Width; col++)
                    {
                        if (IsFree(occupied, grid, col, row, w, h))
                        {
                            x = col;
                            y = row;
                            rotated = rot;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // Marks every cell covered by the children in one grid. Returns null when the container has no grid by
        // that name. Children that are malformed or out of bounds are clipped rather than trusted; the validator
        // reports them.
        private static bool[,] BuildOccupancy(ItemCatalog catalog, ItemNode container, string gridName, string ignoreItemId, out GridSpec grid)
        {
            grid = null;
            if (catalog == null || container == null || !catalog.TryGet(container.DefId, out var containerDef))
            {
                return null;
            }

            grid = containerDef.FindGrid(gridName);
            if (grid == null)
            {
                return null;
            }

            var occupied = new bool[grid.Width, grid.Height];
            if (container.Children == null)
            {
                return occupied;
            }

            foreach (var child in container.Children)
            {
                if (child == null || child.Slot != grid.Name || child.X == null || child.Y == null)
                {
                    continue;
                }

                if (ignoreItemId != null && child.Id == ignoreItemId)
                {
                    continue;
                }

                if (!catalog.TryGet(child.DefId, out var childDef))
                {
                    continue;
                }

                GetSize(childDef, child.Rotated, out int w, out int h);
                for (int cx = Math.Max(0, child.X.Value); cx < Math.Min(grid.Width, child.X.Value + w); cx++)
                {
                    for (int cy = Math.Max(0, child.Y.Value); cy < Math.Min(grid.Height, child.Y.Value + h); cy++)
                    {
                        occupied[cx, cy] = true;
                    }
                }
            }

            return occupied;
        }

        private static bool IsFree(bool[,] occupied, GridSpec grid, int x, int y, int w, int h)
        {
            if (x < 0 || y < 0 || x + w > grid.Width || y + h > grid.Height)
            {
                return false;
            }

            for (int cx = x; cx < x + w; cx++)
            {
                for (int cy = y; cy < y + h; cy++)
                {
                    if (occupied[cx, cy])
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
