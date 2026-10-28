using System;

namespace RaidZero.Shared.InventoryUi
{
    /// <summary>The arithmetic of dragging an item over a grid, in the screen's pixels.</summary>
    public static class DragGeometry
    {
        /// <summary>
        /// The cell the dragged item's top-left corner lands on. The item is held at the point it was grabbed
        /// (<paramref name="grabX"/>, <paramref name="grabY"/> from its top-left), so its corner is the pointer
        /// minus that offset, snapped to the nearest cell. Results can be outside the grid; the drop check
        /// reports those as not fitting.
        /// </summary>
        /// <param name="pointerX">The pointer, relative to the grid's top-left corner.</param>
        public static void AnchorCell(float pointerX, float pointerY, float grabX, float grabY, float cellSize, out int x, out int y)
        {
            if (cellSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cellSize), "Cells need a positive size.");
            }

            x = (int)Math.Floor((pointerX - grabX) / cellSize + 0.5f);
            y = (int)Math.Floor((pointerY - grabY) / cellSize + 0.5f);
        }

        /// <summary>
        /// Where the grab point moves when the dragged item turns (R): the same spot relative to the item's
        /// size, so the item turns under the pointer instead of jumping away from it.
        /// </summary>
        /// <param name="width">The item's size before turning, in pixels.</param>
        public static void RotateGrab(float grabX, float grabY, float width, float height, out float newGrabX, out float newGrabY)
        {
            // After turning, the item is height wide and width tall.
            newGrabX = width > 0 ? grabX / width * height : 0;
            newGrabY = height > 0 ? grabY / height * width : 0;
        }
    }
}
