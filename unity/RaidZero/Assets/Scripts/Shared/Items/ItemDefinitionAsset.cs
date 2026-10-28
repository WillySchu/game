using System;
using System.Collections.Generic;
using RaidZero.Shared.Catalog;
using UnityEngine;

namespace RaidZero.Items
{
    /// <summary>
    /// One kind of item: the rules every part of the game agrees on (size, stacking, slots), plus what the
    /// game shows for it (name, icon, model). These assets are the source of truth: saving one rewrites
    /// shared/catalog/item_catalog.json, which the backend and game servers load.
    ///
    /// Never change an item's def_id once it has existed in anyone's stash; rename the display name instead.
    /// </summary>
    [CreateAssetMenu(menuName = "Raid Zero/Item Definition", fileName = "new_item")]
    public sealed class ItemDefinitionAsset : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Permanent ID: lowercase letters, digits and underscores. Never change it once the item has been in a stash.")]
        [SerializeField]
        private string _defId = "";

        [Tooltip("Shown to players. Safe to change at any time; it isn't part of the rules.")]
        [SerializeField]
        private string _displayName = "";

        [TextArea]
        [SerializeField]
        private string _description = "";

        [Header("Rules (part of the catalog version)")]
        [Tooltip("Footprint in grid cells, unrotated.")]
        [Min(1)]
        [SerializeField]
        private int _width = 1;

        [Min(1)]
        [SerializeField]
        private int _height = 1;

        [Tooltip("1 for items that don't stack.")]
        [Min(1)]
        [SerializeField]
        private int _maxStack = 1;

        [Tooltip("Instances carry a 0..1 condition. Only for items that don't stack.")]
        [SerializeField]
        private bool _hasDurability;

        [Tooltip("Decide which named slots accept this item, e.g. weapon_long or ammo_545.")]
        [SerializeField]
        private List<string> _tags = new List<string>();

        [Tooltip("Holds a grid of other items (backpacks, rigs, crates, the stash).")]
        [SerializeField]
        private bool _hasGrid;

        [SerializeField]
        private Vector2Int _gridSize = new Vector2Int(1, 1);

        [Tooltip("Named slots, such as a rifle's magazine or a character's primary.")]
        [SerializeField]
        private List<Slot> _slots = new List<Slot>();

        [Header("Presentation (not part of the rules)")]
        [SerializeField]
        private Sprite _icon = null;

        [Tooltip("What it looks like in the world: on the ground, in hands, on a body.")]
        [SerializeField]
        private GameObject _worldPrefab = null;

        public string DefId => _defId;

        public string DisplayName => string.IsNullOrEmpty(_displayName) ? _defId : _displayName;

        public string Description => _description;

        public Sprite Icon => _icon;

        public GameObject WorldPrefab => _worldPrefab;

        /// <summary>The rules as the shared code, backend and servers see them.</summary>
        public ItemDefinition ToDefinition()
        {
            var definition = new ItemDefinition
            {
                DefId = _defId,
                DisplayName = _displayName,
                Width = _width,
                Height = _height,
                MaxStack = _maxStack,
                HasDurability = _hasDurability,
                Tags = new List<string>(_tags),
                Grid = _hasGrid ? new GridSpec { Width = _gridSize.x, Height = _gridSize.y } : null,
            };

            foreach (var slot in _slots)
            {
                definition.Slots.Add(new SlotSpec { Name = slot.Name, Accepts = new List<string>(slot.Accepts), MaxQuantity = slot.MaxQuantity });
            }

            return definition;
        }

        /// <summary>Takes the rules and display name from a catalog entry, leaving the presentation fields alone.</summary>
        public void SetRules(ItemDefinition definition)
        {
            _defId = definition.DefId;
            _displayName = definition.DisplayName ?? "";
            _width = definition.Width;
            _height = definition.Height;
            _maxStack = definition.MaxStack;
            _hasDurability = definition.HasDurability;
            _tags = new List<string>(definition.Tags ?? new List<string>());
            _hasGrid = definition.Grid != null;
            _gridSize = definition.Grid != null ? new Vector2Int(definition.Grid.Width, definition.Grid.Height) : new Vector2Int(1, 1);
            _slots = new List<Slot>();
            foreach (var slot in definition.Slots ?? new List<SlotSpec>())
            {
                _slots.Add(new Slot { Name = slot.Name, Accepts = new List<string>(slot.Accepts ?? new List<string>()), MaxQuantity = slot.MaxQuantity });
            }
        }

        [Serializable]
        public sealed class Slot
        {
            public string Name = "";

            [Tooltip("Accepts an item carrying any one of these tags.")]
            public List<string> Accepts = new List<string>();

            [Tooltip("Caps the stack in this slot below the item's own max stack, e.g. 30 rounds in a magazine. 0 for no cap.")]
            [Min(0)]
            public int MaxQuantity;
        }
    }
}
