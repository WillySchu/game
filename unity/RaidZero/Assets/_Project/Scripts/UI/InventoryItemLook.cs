using RaidZero.Items;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.InventoryUi;
using UnityEngine;
using UnityEngine.UIElements;

namespace RaidZero.UI
{
    /// <summary>
    /// What an item looks like on the inventory screen, for the items in the panels and the ghost that follows a
    /// drag alike. With an icon (the item asset's, hand-made or rendered by Raid Zero > Items > Render Icons) it
    /// shows the picture with a small name over it; without one, the plain look: its category in small capitals
    /// and its name, on its category's colour. Either way the count sits in the corner.
    /// </summary>
    internal static class InventoryItemLook
    {
        /// <summary>The item asset's icon, or null for the plain look.</summary>
        public static Sprite IconOf(string defId) => ItemLibrary.Instance != null ? ItemLibrary.Instance.Find(defId)?.Icon : null;

        /// <summary>
        /// Clears the element and fills it for the item. Width and height are the element's size on screen; a
        /// rotated item's icon is turned to match its footprint.
        /// </summary>
        public static void Fill(VisualElement element, ItemCatalog catalog, ItemNode node, ItemDefinition def, bool rotated, float width, float height, string count)
        {
            element.Clear();
            element.ClearClassList();
            element.AddToClassList("inv-item");
            element.AddToClassList("inv-item--" + ItemBadges.Category(def));

            var name = def != null ? (string.IsNullOrEmpty(def.DisplayName) ? def.DefId : def.DisplayName) : node?.DefId;
            var icon = IconOf(def?.DefId ?? node?.DefId);
            if (icon != null)
            {
                element.AddToClassList("inv-item--has-icon");
                var picture = new VisualElement { name = "icon", pickingMode = PickingMode.Ignore }.WithClass("inv-item__icon");
                picture.style.backgroundImage = new StyleBackground(icon);
                element.Add(picture);
                LayoutIcon(element, rotated, width, height);
            }
            else
            {
                element.Add(new Label(ItemTooltip.CategoryName(def).ToUpperInvariant()) { pickingMode = PickingMode.Ignore }.WithClass("inv-item__kind"));
            }

            element.Add(new Label(name) { pickingMode = PickingMode.Ignore }.WithClass("inv-item__name"));
            if (!string.IsNullOrEmpty(count))
            {
                element.Add(new Label(count) { pickingMode = PickingMode.Ignore }.WithClass("inv-item__count"));
            }
        }

        /// <summary>
        /// Fits the icon to the element. Unturned, it fills the item (the stylesheet does that). Icons are drawn for
        /// the item's unrotated footprint, so a rotated item's icon is laid out at the swapped size, centred, and
        /// turned a quarter.
        /// </summary>
        public static void LayoutIcon(VisualElement element, bool rotated, float width, float height)
        {
            var picture = element.Q("icon");
            if (picture == null)
            {
                return;
            }

            picture.EnableInClassList("inv-item__icon--rotated", rotated);
            if (!rotated)
            {
                picture.style.width = StyleKeyword.Null;
                picture.style.height = StyleKeyword.Null;
                picture.style.left = StyleKeyword.Null;
                picture.style.top = StyleKeyword.Null;
                return;
            }

            // Inside the item's border, which InventoryScreen.uss sets to ItemBorder.
            var inner = new Vector2(width - 2 * ItemBorder, height - 2 * ItemBorder);
            picture.style.width = inner.y;
            picture.style.height = inner.x;
            picture.style.left = (inner.x - inner.y) / 2f;
            picture.style.top = (inner.y - inner.x) / 2f;
        }

        /// <summary>The width of .inv-item's border in InventoryScreen.uss. Change both together.</summary>
        private const float ItemBorder = 1f;
    }
}
