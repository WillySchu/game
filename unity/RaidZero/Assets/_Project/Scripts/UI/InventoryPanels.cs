using System;
using System.Collections.Generic;
using RaidZero.Items;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.InventoryUi;
using UnityEngine;
using UnityEngine.UIElements;

namespace RaidZero.UI
{
    /// <summary>What a grid element stands for. Hit testing walks up from the picked element to find one of these.</summary>
    internal sealed class GridRef
    {
        public string ContainerId;
        public string GridName;
    }

    /// <summary>What a named-slot box stands for: the primary slot of the character, say.</summary>
    internal sealed class SlotRef
    {
        public string ParentId;
        public string SlotName;
    }

    /// <summary>What an item element stands for.</summary>
    internal sealed class ItemRef
    {
        public string ItemId;
    }

    /// <summary>
    /// Builds the inventory screen's panels from the model: the player's gear on the left (slot boxes, then the
    /// grids of whatever's in them, like the rig and backpack) and everything they have open on the right. All
    /// the look is in InventoryScreen.uss; this only builds the elements, sizes the ones that depend on the
    /// item data, and tags them with GridRef, SlotRef and ItemRef so the drag code knows what's under the pointer.
    /// </summary>
    internal sealed class InventoryPanels
    {
        /// <summary>One grid cell, in panel units (pixels at the 1920x1080 reference resolution).</summary>
        public const float Cell = 52f;

        /// <summary>Space between a container's pockets, in cells.</summary>
        public const float PocketGap = 0.25f;

        /// <summary>How many cells wide each column's grids may get before pockets wrap.</summary>
        public const int GearColumnCells = 7;

        public const int LootColumnCells = 10;

        // Named-slot boxes, in cells. Anything not listed gets the default.
        private static readonly Dictionary<string, Vector2Int> SlotSizes = new Dictionary<string, Vector2Int>
        {
            { SlotNames.Primary, new Vector2Int(5, 2) },
            { SlotNames.Secondary, new Vector2Int(2, 2) },
        };

        private static readonly Vector2Int DefaultSlotSize = new Vector2Int(2, 2);

        private readonly VisualElement _gear;
        private readonly VisualElement _loot;
        private readonly Dictionary<string, VisualElement> _items = new Dictionary<string, VisualElement>();
        private readonly Dictionary<string, VisualElement> _slots = new Dictionary<string, VisualElement>();
        private readonly Dictionary<string, VisualElement> _grids = new Dictionary<string, VisualElement>();

        public InventoryPanels(VisualElement gear, VisualElement loot)
        {
            _gear = gear;
            _loot = loot;
        }

        /// <summary>Pointer pressed on an item. The drag code decides what that means.</summary>
        public event Action<PointerDownEvent, VisualElement> ItemPressed;

        /// <summary>A panel's close button was clicked: a root's ID, or an opened item's (InventoryModel.IsOpenedItem).</summary>
        public event Action<string> CloseClicked;

        public VisualElement FindItem(string itemId) => itemId != null && _items.TryGetValue(itemId, out var e) ? e : null;

        public VisualElement FindSlot(string parentId, string slot) => parentId != null && _slots.TryGetValue(Key(parentId, slot), out var e) ? e : null;

        public VisualElement FindGrid(string containerId, string grid) => containerId != null && _grids.TryGetValue(Key(containerId, grid), out var e) ? e : null;

        /// <summary>Throw away the old elements and build them again from the model as it is now.</summary>
        public void Build(InventoryModel model, string emptyMessage)
        {
            _gear.Clear();
            _loot.Clear();
            _items.Clear();
            _slots.Clear();
            _grids.Clear();

            var character = model?.Character;
            if (character == null)
            {
                _gear.Add(new Label(emptyMessage).WithClass("inv-empty"));
                return;
            }

            BuildLoadout(_gear, model, character.Root, "Gear", closable: false);

            // Containers opened from inside others (double-click) first: the one you just opened is on top.
            foreach (var openedId in model.OpenedItems)
            {
                if (model.Tree.TryGet(openedId, out var opened) && model.Catalog.TryGet(opened.DefId, out var openedDef))
                {
                    var panel = Panel(_loot, NameOf(openedDef), opened.Id, closable: true);
                    panel.AddToClassList("inv-panel--opened");
                    BuildGrids(panel, model, opened, LootColumnCells);
                }
            }

            foreach (var other in model.Others)
            {
                if (other.Kind == InventoryRootKind.Body)
                {
                    BuildLoadout(_loot, model, other.Root, "Body", closable: true);
                }
                else
                {
                    // The stash (in the Hideout) stays open; crates, piles and the like can be closed.
                    var panel = Panel(_loot, TitleOf(model.Catalog, other), other.Root.Id, closable: other.Kind != InventoryRootKind.Stash);
                    BuildGrids(panel, model, other.Root, LootColumnCells);
                }
            }

            if (_loot.childCount == 0)
            {
                _loot.Add(new Label("Nothing open. F opens the nearest crate.").WithClass("inv-empty"));
            }
        }

        // A character or a body: its named slots, then a panel for each slotted item that has grids (rig, backpack).
        private void BuildLoadout(VisualElement column, InventoryModel model, ItemNode root, string title, bool closable)
        {
            var panel = Panel(column, title, root.Id, closable);
            if (!model.Catalog.TryGet(root.DefId, out var def))
            {
                return;
            }

            var slots = new VisualElement().WithClass("inv-slots");
            panel.Add(slots);
            foreach (var spec in def.Slots ?? new List<SlotSpec>())
            {
                var size = SlotSizes.TryGetValue(spec.Name, out var s) ? s : DefaultSlotSize;
                var holder = new VisualElement().WithClass("inv-slot-holder");
                holder.Add(new Label(Title(spec.Name)).WithClass("inv-slot__caption"));

                var box = new VisualElement { userData = new SlotRef { ParentId = root.Id, SlotName = spec.Name } }.WithClass("inv-slot");
                box.style.width = size.x * Cell;
                box.style.height = size.y * Cell;
                _slots[Key(root.Id, spec.Name)] = box;
                holder.Add(box);
                slots.Add(holder);

                var occupant = Occupant(root, spec.Name);
                if (occupant != null)
                {
                    box.Add(ItemElement(model, occupant).WithClass("inv-item--in-slot"));
                }
            }

            foreach (var spec in def.Slots ?? new List<SlotSpec>())
            {
                var occupant = Occupant(root, spec.Name);
                if (occupant != null && model.Catalog.TryGet(occupant.DefId, out var occupantDef) && occupantDef.HasGrids())
                {
                    var sub = Panel(column, Title(spec.Name) + ": " + NameOf(occupantDef), null, closable: false);
                    sub.AddToClassList("inv-panel--nested");
                    BuildGrids(sub, model, occupant, GearColumnCells);
                }
            }
        }

        // A container's grids, placed by PocketLayout: where the item's asset says, or flowing in fill order.
        private void BuildGrids(VisualElement panel, InventoryModel model, ItemNode container, int maxRowCells)
        {
            if (!model.Catalog.TryGet(container.DefId, out var def) || !def.HasGrids())
            {
                return;
            }

            var asset = ItemLibrary.Instance != null ? ItemLibrary.Instance.Find(def.DefId) : null;
            var specs = new List<PocketSpec>();
            foreach (var grid in def.Grids)
            {
                if (asset != null && asset.TryGetScreenPosition(grid.Name, out var at))
                {
                    specs.Add(new PocketSpec(grid.Name, grid.Width, grid.Height, at.x, at.y));
                }
                else
                {
                    specs.Add(new PocketSpec(grid.Name, grid.Width, grid.Height));
                }
            }

            var arrangement = PocketLayout.Arrange(specs, maxRowCells, PocketGap);
            var area = new VisualElement().WithClass("inv-grids");
            area.style.width = arrangement.Width * Cell;
            area.style.height = arrangement.Height * Cell;
            panel.Add(area);

            foreach (var placement in arrangement.Placements)
            {
                var grid = new VisualElement { userData = new GridRef { ContainerId = container.Id, GridName = placement.Name } }.WithClass("inv-grid");
                Place(grid, placement.X, placement.Y, placement.Width, placement.Height);
                grid.tooltip = def.Grids.Count > 1 ? placement.Name : null;
                _grids[Key(container.Id, placement.Name)] = grid;
                area.Add(grid);

                for (int y = 0; y < placement.Height; y++)
                {
                    for (int x = 0; x < placement.Width; x++)
                    {
                        var cell = new VisualElement { pickingMode = PickingMode.Ignore }.WithClass("inv-cell");
                        Place(cell, x, y, 1, 1);
                        grid.Add(cell);
                    }
                }

                foreach (var child in container.Children)
                {
                    if (child.Slot != placement.Name || !child.X.HasValue || !child.Y.HasValue || !model.Catalog.TryGet(child.DefId, out var childDef))
                    {
                        continue;
                    }

                    GridPlacement.GetSize(childDef, child.Rotated, out int w, out int h);
                    var item = ItemElement(model, child);
                    Place(item, child.X.Value, child.Y.Value, w, h);
                    grid.Add(item);
                }
            }
        }

        private VisualElement Panel(VisualElement column, string title, string rootId, bool closable)
        {
            var panel = new VisualElement().WithClass("inv-panel");
            var header = new VisualElement().WithClass("inv-panel__header");
            header.Add(new Label(title).WithClass("inv-panel__title"));
            if (closable && rootId != null)
            {
                var close = new Button(() => CloseClicked?.Invoke(rootId)) { text = "×", tooltip = "Close" }.WithClass("inv-panel__close");
                header.Add(close);
            }

            panel.Add(header);
            column.Add(panel);
            return panel;
        }

        private VisualElement ItemElement(InventoryModel model, ItemNode node)
        {
            model.Catalog.TryGet(node.DefId, out var def);
            var element = new VisualElement { userData = new ItemRef { ItemId = node.Id }, tooltip = def != null ? NameOf(def) : node.DefId }
                .WithClass("inv-item")
                .WithClass("inv-item--" + ItemBadges.Category(def));
            element.EnableInClassList("inv-item--pending", model.IsPending(node.Id));
            element.EnableInClassList("inv-item--rotated", node.Rotated);
            element.EnableInClassList("inv-item--opened", model.IsOpenedItem(node.Id));
            element.Add(new Label(def != null ? NameOf(def) : node.DefId).WithClass("inv-item__name"));

            var count = ItemBadges.Count(model.Catalog, node);
            if (count.Length > 0)
            {
                element.Add(new Label(count).WithClass("inv-item__count"));
            }

            element.RegisterCallback<PointerDownEvent>(evt => ItemPressed?.Invoke(evt, element));
            _items[node.Id] = element;
            return element;
        }

        /// <summary>Position an absolutely placed element on the cell grid.</summary>
        public static void Place(VisualElement element, float x, float y, float width, float height)
        {
            element.style.position = Position.Absolute;
            element.style.left = x * Cell;
            element.style.top = y * Cell;
            element.style.width = width * Cell;
            element.style.height = height * Cell;
        }

        private static string TitleOf(ItemCatalog catalog, InventoryRoot root)
        {
            switch (root.Kind)
            {
                case InventoryRootKind.Ground:
                    return "Ground";
                case InventoryRootKind.Stash:
                    return "Stash";
                default:
                    return catalog.TryGet(root.Root.DefId, out var def) ? NameOf(def) : root.Root.DefId;
            }
        }

        private static string NameOf(ItemDefinition def) => string.IsNullOrEmpty(def.DisplayName) ? def.DefId : def.DisplayName;

        private static string Title(string name) => string.IsNullOrEmpty(name) ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);

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

        private static string Key(string id, string name) => id + "/" + name;
    }

    internal static class ElementExtensions
    {
        public static T WithClass<T>(this T element, string className) where T : VisualElement
        {
            element.AddToClassList(className);
            return element;
        }
    }
}
