using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RaidZero.Shared.Catalog;

namespace RaidZero.BackendApi
{
    /// <summary>
    /// Reads and writes item_catalog.json in Unity, where System.Text.Json isn't available. It reads the same snake_case
    /// fields as the backend's loader, by hand rather than by reflection, so IL2CPP stripping can't break it.
    /// Like the backend, it refuses a file whose declared catalog_version doesn't match its contents: a server
    /// running on the wrong rules must fail at startup, not halfway through a raid.
    /// </summary>
    public static class CatalogJson
    {
        public static ItemCatalog LoadFile(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Item catalog not found at '" + Path.GetFullPath(path) + "'.", path);
            }

            return Parse(File.ReadAllText(path), path);
        }

        /// <param name="source">Where the JSON came from, for error messages.</param>
        public static ItemCatalog Parse(string json, string source = "item_catalog.json")
        {
            JObject document;
            try
            {
                document = Wire.Parse(json) as JObject ?? throw new InvalidDataException(source + " isn't a JSON object.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(source + " isn't valid JSON: " + exception.Message, exception);
            }

            if (!(document["items"] is JArray items) || items.Count == 0)
            {
                throw new InvalidDataException(source + " has no items.");
            }

            var definitions = new List<ItemDefinition>();
            foreach (var token in items)
            {
                if (!(token is JObject item))
                {
                    throw new InvalidDataException(source + " has an item that isn't an object.");
                }

                var definition = new ItemDefinition
                {
                    DefId = (string)item["def_id"],
                    DisplayName = (string)item["display_name"],
                    Width = (int?)item["width"] ?? 1,
                    Height = (int?)item["height"] ?? 1,
                    MaxStack = (int?)item["max_stack"] ?? 1,
                    HasDurability = (bool?)item["has_durability"] ?? false,
                    Tags = Strings(item["tags"]),
                };

                if (item["grid"] != null)
                {
                    // The format before named grids. Read as today's, the item would silently have no grid.
                    throw new InvalidDataException(source + " is in the old format (\"grid\" instead of \"grids\"). Re-export the catalog from Unity.");
                }

                if (item["grids"] is JArray grids)
                {
                    foreach (var gridToken in grids)
                    {
                        if (gridToken is JObject grid)
                        {
                            definition.Grids.Add(new GridSpec
                            {
                                Name = (string)grid["name"],
                                Width = (int?)grid["width"] ?? 0,
                                Height = (int?)grid["height"] ?? 0,
                            });
                        }
                    }
                }

                if (item["slots"] is JArray slots)
                {
                    foreach (var slotToken in slots)
                    {
                        if (slotToken is JObject slot)
                        {
                            definition.Slots.Add(new SlotSpec
                            {
                                Name = (string)slot["name"],
                                Accepts = Strings(slot["accepts"]),
                                MaxQuantity = (int?)slot["max_quantity"] ?? 0,
                            });
                        }
                    }
                }

                definitions.Add(definition);
            }

            var catalog = new ItemCatalog(definitions);
            var declared = (string)document["catalog_version"];
            if (declared != catalog.Version)
            {
                throw new InvalidDataException(source + " declares catalog_version '" + declared + "' but its contents hash to '" + catalog.Version + "'.");
            }

            return catalog;
        }

        /// <summary>
        /// Writes item_catalog.json the way the Unity exporter does: items sorted by def_id, fields that are
        /// at their defaults left out, and catalog_version computed from the contents. The same definitions
        /// always produce the same text, so an export with no rule changes is no diff at all.
        /// </summary>
        public static string Write(IEnumerable<ItemDefinition> definitions)
        {
            var list = new List<ItemDefinition>(definitions ?? throw new ArgumentNullException(nameof(definitions)));
            var catalog = new ItemCatalog(list);
            list.Sort((a, b) => string.CompareOrdinal(a.DefId, b.DefId));

            var items = new JArray();
            foreach (var definition in list)
            {
                var item = new JObject { ["def_id"] = definition.DefId };
                if (!string.IsNullOrEmpty(definition.DisplayName))
                {
                    item["display_name"] = definition.DisplayName;
                }

                if (definition.Width != 1)
                {
                    item["width"] = definition.Width;
                }

                if (definition.Height != 1)
                {
                    item["height"] = definition.Height;
                }

                if (definition.MaxStack != 1)
                {
                    item["max_stack"] = definition.MaxStack;
                }

                if (definition.HasDurability)
                {
                    item["has_durability"] = true;
                }

                if (definition.Tags != null && definition.Tags.Count > 0)
                {
                    item["tags"] = new JArray(definition.Tags);
                }

                if (definition.Grids != null && definition.Grids.Count > 0)
                {
                    // In definition order, which is the order free spots are filled in.
                    var grids = new JArray();
                    foreach (var grid in definition.Grids)
                    {
                        grids.Add(new JObject { ["name"] = grid.Name, ["width"] = grid.Width, ["height"] = grid.Height });
                    }

                    item["grids"] = grids;
                }

                if (definition.Slots != null && definition.Slots.Count > 0)
                {
                    var slots = new JArray();
                    foreach (var slot in definition.Slots)
                    {
                        var json = new JObject { ["name"] = slot.Name, ["accepts"] = new JArray(slot.Accepts ?? new List<string>()) };
                        if (slot.MaxQuantity != 0)
                        {
                            json["max_quantity"] = slot.MaxQuantity;
                        }

                        slots.Add(json);
                    }

                    item["slots"] = slots;
                }

                items.Add(item);
            }

            var document = new JObject { ["catalog_version"] = catalog.Version, ["items"] = items };
            return document.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";
        }

        private static List<string> Strings(JToken token)
        {
            var list = new List<string>();
            if (token is JArray array)
            {
                foreach (var value in array)
                {
                    list.Add((string)value);
                }
            }

            return list;
        }
    }
}
