using System;
using System.Collections.Generic;
using UnityEngine;

namespace RaidZero.Items
{
    /// <summary>
    /// Every item definition, for looking up how to show an item from its def_id: its name, icon and model.
    /// The exporter keeps the list up to date, so never edit it by hand. It lives in a Resources folder so
    /// builds include it and any code can reach it through <see cref="Instance"/>.
    /// </summary>
    public sealed class ItemLibrary : ScriptableObject
    {
        /// <summary>The asset's name under Resources, which the exporter also uses.</summary>
        public const string ResourceName = "ItemLibrary";

        private static ItemLibrary _instance;

        [SerializeField]
        private List<ItemDefinitionAsset> _items = new List<ItemDefinitionAsset>();

        private Dictionary<string, ItemDefinitionAsset> _byId;

        /// <summary>The library, or null if no export has created it yet.</summary>
        public static ItemLibrary Instance => _instance != null ? _instance : (_instance = Resources.Load<ItemLibrary>(ResourceName));

        public IReadOnlyList<ItemDefinitionAsset> Items => _items;

        public ItemDefinitionAsset Find(string defId)
        {
            if (_byId == null)
            {
                _byId = new Dictionary<string, ItemDefinitionAsset>(StringComparer.Ordinal);
                foreach (var item in _items)
                {
                    if (item != null && !string.IsNullOrEmpty(item.DefId))
                    {
                        _byId[item.DefId] = item;
                    }
                }
            }

            return defId != null && _byId.TryGetValue(defId, out var found) ? found : null;
        }

        /// <summary>The name to show, falling back to the def_id for items without an asset.</summary>
        public static string DisplayNameOf(string defId) => Instance?.Find(defId)?.DisplayName ?? defId;

        /// <summary>For the exporter.</summary>
        public void SetItems(List<ItemDefinitionAsset> items)
        {
            _items = items;
            _byId = null;
        }
    }
}
