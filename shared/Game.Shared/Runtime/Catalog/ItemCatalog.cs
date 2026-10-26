using System;
using System.Collections.Generic;
using System.Linq;

namespace RaidZero.Shared.Catalog
{
    /// <summary>
    /// The validated, read-only set of item definitions. Construction checks every definition and
    /// throws <see cref="CatalogException"/> listing all problems at once, so a bad export fails
    /// loudly at startup instead of corrupting inventories later.
    /// </summary>
    public sealed class ItemCatalog
    {
        /// <summary>Slot name used for grid placement. A definition may not declare a named slot with it.</summary>
        public const string GridSlotName = "grid";

        private readonly Dictionary<string, ItemDefinition> _byId;

        public ItemCatalog(IEnumerable<ItemDefinition> definitions)
        {
            if (definitions == null)
            {
                throw new ArgumentNullException(nameof(definitions));
            }

            var list = definitions.ToList();
            var errors = Validate(list);
            if (errors.Count > 0)
            {
                throw new CatalogException(errors);
            }

            _byId = list.ToDictionary(d => d.DefId, StringComparer.Ordinal);
            Version = CatalogHash.Compute(list);
        }

        /// <summary>The computed version hash of these definitions.</summary>
        public string Version { get; }

        public int Count => _byId.Count;

        public IEnumerable<ItemDefinition> All => _byId.Values;

        public bool TryGet(string defId, out ItemDefinition definition)
        {
            if (defId == null)
            {
                definition = null;
                return false;
            }

            return _byId.TryGetValue(defId, out definition);
        }

        public ItemDefinition Get(string defId)
        {
            if (!TryGet(defId, out var definition))
            {
                throw new KeyNotFoundException("Unknown item definition '" + defId + "'.");
            }

            return definition;
        }

        public bool Contains(string defId)
        {
            return defId != null && _byId.ContainsKey(defId);
        }

        /// <summary>Returns every problem with a set of definitions; empty when they are valid.</summary>
        public static List<string> Validate(IReadOnlyList<ItemDefinition> definitions)
        {
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < definitions.Count; i++)
            {
                var def = definitions[i];
                if (def == null)
                {
                    errors.Add("items[" + i + "] is null");
                    continue;
                }

                string where = string.IsNullOrEmpty(def.DefId) ? "items[" + i + "]" : def.DefId;

                if (!IsIdentifier(def.DefId))
                {
                    errors.Add(where + ": def_id must be lowercase letters, digits and underscores");
                }
                else if (!seen.Add(def.DefId))
                {
                    errors.Add(where + ": duplicate def_id");
                }

                if (def.Width < 1 || def.Height < 1)
                {
                    errors.Add(where + ": width and height must be at least 1");
                }

                if (def.MaxStack < 1)
                {
                    errors.Add(where + ": max_stack must be at least 1");
                }

                if (def.HasDurability && def.MaxStack > 1)
                {
                    errors.Add(where + ": stackable items cannot have durability");
                }

                foreach (var tag in def.Tags ?? new List<string>())
                {
                    if (!IsIdentifier(tag))
                    {
                        errors.Add(where + ": tag '" + tag + "' must be lowercase letters, digits and underscores");
                    }
                }

                if (def.Grid != null && (def.Grid.Width < 1 || def.Grid.Height < 1))
                {
                    errors.Add(where + ": grid width and height must be at least 1");
                }

                var slotNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var slot in def.Slots ?? new List<SlotSpec>())
                {
                    if (slot == null || !IsIdentifier(slot.Name))
                    {
                        errors.Add(where + ": slot names must be lowercase letters, digits and underscores");
                        continue;
                    }

                    if (slot.Name == GridSlotName)
                    {
                        errors.Add(where + ": slot name 'grid' is reserved; use the grid field instead");
                    }

                    if (!slotNames.Add(slot.Name))
                    {
                        errors.Add(where + ": duplicate slot '" + slot.Name + "'");
                    }

                    if (slot.Accepts == null || slot.Accepts.Count == 0)
                    {
                        errors.Add(where + ": slot '" + slot.Name + "' accepts nothing");
                    }

                    if (slot.MaxQuantity < 0)
                    {
                        errors.Add(where + ": slot '" + slot.Name + "' max_quantity cannot be negative");
                    }
                }
            }

            return errors;
        }

        private static bool IsIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
            {
                return false;
            }

            foreach (char c in value)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public sealed class CatalogException : Exception
    {
        public CatalogException(IReadOnlyList<string> errors)
            : base("Invalid item catalog:\n  " + string.Join("\n  ", errors))
        {
            Errors = errors;
        }

        public IReadOnlyList<string> Errors { get; }
    }
}
