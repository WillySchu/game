using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RaidZero.Shared.Catalog;

namespace RaidZero.BackendApi
{
    /// <summary>
    /// Reads item_catalog.json in Unity, where System.Text.Json isn't available. It reads the same snake_case
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

                if (item["grid"] is JObject grid)
                {
                    definition.Grid = new GridSpec { Width = (int?)grid["width"] ?? 0, Height = (int?)grid["height"] ?? 0 };
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
