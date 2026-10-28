using System;
using System.Collections.Generic;

namespace RaidZero.Shared.InventoryUi
{
    /// <summary>One of a container's grids, as the screen needs to place it.</summary>
    public sealed class PocketSpec
    {
        public PocketSpec(string name, int width, int height, float? x = null, float? y = null)
        {
            Name = name;
            Width = width;
            Height = height;
            X = x;
            Y = y;
        }

        public string Name { get; }

        /// <summary>Size in cells.</summary>
        public int Width { get; }

        public int Height { get; }

        /// <summary>Where the item's asset says to draw it, in cells from the panel's top-left, or null to place it automatically.</summary>
        public float? X { get; }

        public float? Y { get; }
    }

    /// <summary>Where one grid is drawn, in cells from the top-left of its container's panel.</summary>
    public sealed class PocketPlacement
    {
        public PocketPlacement(string name, float x, float y, int width, int height)
        {
            Name = name;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public string Name { get; }

        public float X { get; }

        public float Y { get; }

        public int Width { get; }

        public int Height { get; }
    }

    /// <summary>Every grid of a container placed, and the area they take up together.</summary>
    public sealed class PocketArrangement
    {
        public PocketArrangement(IReadOnlyList<PocketPlacement> placements, float width, float height)
        {
            Placements = placements;
            Width = width;
            Height = height;
        }

        public IReadOnlyList<PocketPlacement> Placements { get; }

        /// <summary>The panel area the grids need, in cells.</summary>
        public float Width { get; }

        public float Height { get; }
    }

    /// <summary>
    /// Where a container's grids go on screen. That's presentation, separate from the rules: the order of a
    /// container's grids decides which fills first, while their screen positions come from the item's asset.
    /// Grids with a position go there. The rest flow left to right in their order, below any placed ones,
    /// wrapping to a new row when the next wouldn't fit in <c>maxRowWidth</c> cells.
    /// </summary>
    public static class PocketLayout
    {
        /// <param name="maxRowWidth">How wide a row of automatically placed grids may get, in cells.</param>
        /// <param name="gap">Space between automatically placed grids, in cells (a fraction, typically).</param>
        public static PocketArrangement Arrange(IReadOnlyList<PocketSpec> pockets, float maxRowWidth, float gap)
        {
            if (pockets == null)
            {
                throw new ArgumentNullException(nameof(pockets));
            }

            var placements = new List<PocketPlacement>(pockets.Count);
            float width = 0;
            float height = 0;

            // The placed ones first, so the flow can start below them.
            foreach (var pocket in pockets)
            {
                if (pocket.X.HasValue && pocket.Y.HasValue)
                {
                    width = Math.Max(width, pocket.X.Value + pocket.Width);
                    height = Math.Max(height, pocket.Y.Value + pocket.Height);
                }
            }

            float x = 0;
            float y = height > 0 ? height + gap : 0;
            float rowHeight = 0;
            foreach (var pocket in pockets)
            {
                if (pocket.X.HasValue && pocket.Y.HasValue)
                {
                    placements.Add(new PocketPlacement(pocket.Name, pocket.X.Value, pocket.Y.Value, pocket.Width, pocket.Height));
                    continue;
                }

                if (x > 0 && x + pocket.Width > maxRowWidth)
                {
                    x = 0;
                    y += rowHeight + gap;
                    rowHeight = 0;
                }

                placements.Add(new PocketPlacement(pocket.Name, x, y, pocket.Width, pocket.Height));
                width = Math.Max(width, x + pocket.Width);
                height = Math.Max(height, y + pocket.Height);
                rowHeight = Math.Max(rowHeight, pocket.Height);
                x += pocket.Width + gap;
            }

            return new PocketArrangement(placements, width, height);
        }
    }
}
