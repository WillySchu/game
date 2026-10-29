using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RaidZero.BackendApi;
using RaidZero.Shared.Catalog;
using UnityEditor;
using UnityEngine;

namespace RaidZero.Items.Editor
{
    /// <summary>
    /// Keeps shared/catalog/item_catalog.json in step with the item definition assets. Saving an item
    /// exports the catalog by itself; the menu items are for doing it by hand.
    ///
    /// Raid Zero > Items > Export Catalog: writes the JSON from the assets, and refreshes the ItemLibrary.
    /// Raid Zero > Items > Import Catalog: the other way, creating or updating assets from the JSON. Use it
    /// once to create the assets, or to bring in a change someone made to the JSON by hand.
    /// </summary>
    public static class ItemCatalogTools
    {
        /// <summary>Where new item assets go. Saving anything here triggers an export.</summary>
        public const string ItemsFolder = "Assets/_Project/Data/Items";

        public const string LibraryFolder = "Assets/_Project/Data/Resources";

        private static readonly string[] RequiredDefIds = { WellKnownDefs.Stash, WellKnownDefs.Character, WellKnownDefs.Ground };

        public static string CatalogPath => RepoPaths.CatalogFile;

        [MenuItem("Raid Zero/Items/Export Catalog")]
        private static void ExportFromMenu() => Export(interactive: true);

        [MenuItem("Raid Zero/Items/Import Catalog")]
        private static void ImportFromMenu() => Import();

        /// <summary>
        /// Validates the assets and writes the catalog if anything changed. Refuses, without writing anything,
        /// if the catalog would be invalid. Removing a def_id that's in the current file needs confirming in
        /// the interactive export, because items of that kind in stashes would stop loading.
        /// </summary>
        public static bool Export(bool interactive)
        {
            var assets = FindAll();
            var errors = new List<string>();
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var asset in assets)
            {
                var path = AssetDatabase.GetAssetPath(asset);
                if (string.IsNullOrEmpty(asset.DefId))
                {
                    errors.Add(path + " has no def_id.");
                }
                else if (paths.TryGetValue(asset.DefId, out var other))
                {
                    errors.Add("def_id '" + asset.DefId + "' is used by both " + other + " and " + path + ".");
                }
                else
                {
                    paths[asset.DefId] = path;
                }
            }

            foreach (var required in RequiredDefIds)
            {
                if (!paths.ContainsKey(required))
                {
                    errors.Add("There's no '" + required + "' item, which the backend and game servers need.");
                }
            }

            var definitions = assets.Where(a => !string.IsNullOrEmpty(a.DefId)).Select(a => a.ToDefinition()).ToList();
            ItemCatalog catalog = null;
            if (errors.Count == 0)
            {
                try
                {
                    catalog = new ItemCatalog(definitions);
                }
                catch (CatalogException exception)
                {
                    errors.AddRange(exception.Errors);
                }
            }

            if (errors.Count > 0)
            {
                Report("The item catalog wasn't exported:\n- " + string.Join("\n- ", errors), interactive);
                return false;
            }

            var previous = TryLoadCurrent();
            var removed = previous == null
                ? new List<string>()
                : previous.All.Select(d => d.DefId).Where(id => !paths.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (removed.Count > 0)
            {
                var message = "These items would leave the catalog: " + string.Join(", ", removed) +
                    ". Any of them already in a stash would stop the backend from loading that stash.";
                if (!interactive)
                {
                    Debug.LogError("[items] " + message + " Not exported. Run Raid Zero > Items > Export Catalog to confirm.");
                    return false;
                }

                if (!EditorUtility.DisplayDialog("Remove items from the catalog?", message, "Remove them", "Cancel"))
                {
                    return false;
                }
            }

            var text = CatalogJson.Write(definitions);
            var current = File.Exists(CatalogPath) ? File.ReadAllText(CatalogPath).Replace("\r\n", "\n") : null;
            if (text != current)
            {
                File.WriteAllText(CatalogPath, text);
                if (previous != null && previous.Version != catalog.Version)
                {
                    Debug.LogWarning("[items] Item rules changed: catalog " + previous.Version + " is now " + catalog.Version +
                        ". The backend and game servers must be deployed from the same commit, or servers will be turned away.");
                }
                else
                {
                    Debug.Log("[items] Exported " + definitions.Count + " items to " + CatalogPath + ".");
                }
            }

            UpdateLibrary(assets);
            return true;
        }

        /// <summary>Creates an asset for every item in the JSON that doesn't have one, and updates the rest.</summary>
        public static void Import()
        {
            ItemCatalog catalog;
            try
            {
                catalog = CatalogJson.LoadFile(CatalogPath);
            }
            catch (Exception exception)
            {
                Report("Couldn't read the catalog: " + exception.Message, true);
                return;
            }

            EnsureFolder(ItemsFolder);
            var existing = FindAll().Where(a => !string.IsNullOrEmpty(a.DefId)).GroupBy(a => a.DefId).ToDictionary(g => g.Key, g => g.First());
            int created = 0;
            foreach (var definition in catalog.All.OrderBy(d => d.DefId, StringComparer.Ordinal))
            {
                if (existing.TryGetValue(definition.DefId, out var asset))
                {
                    asset.SetRules(definition);
                    EditorUtility.SetDirty(asset);
                }
                else
                {
                    asset = ScriptableObject.CreateInstance<ItemDefinitionAsset>();
                    asset.SetRules(definition);
                    AssetDatabase.CreateAsset(asset, ItemsFolder + "/" + definition.DefId + ".asset");
                    created++;
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[items] Imported " + catalog.Count + " items (" + created + " new) from " + CatalogPath + ".");
            Export(interactive: true);
        }

        internal static bool IsItemAsset(string path) =>
            path.EndsWith(".asset", StringComparison.Ordinal) && AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(ItemDefinitionAsset);

        internal static List<ItemDefinitionAsset> FindAll() =>
            AssetDatabase.FindAssets("t:" + nameof(ItemDefinitionAsset))
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<ItemDefinitionAsset>)
                .Where(a => a != null)
                .OrderBy(a => a.DefId, StringComparer.Ordinal)
                .ToList();

        internal static ItemCatalog TryLoadCurrent()
        {
            try
            {
                return File.Exists(CatalogPath) ? CatalogJson.LoadFile(CatalogPath) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void UpdateLibrary(List<ItemDefinitionAsset> assets)
        {
            var path = LibraryFolder + "/" + ItemLibrary.ResourceName + ".asset";
            var library = AssetDatabase.LoadAssetAtPath<ItemLibrary>(path);
            if (library == null)
            {
                EnsureFolder(LibraryFolder);
                library = ScriptableObject.CreateInstance<ItemLibrary>();
                AssetDatabase.CreateAsset(library, path);
            }

            if (!library.Items.SequenceEqual(assets))
            {
                library.SetItems(assets);
                EditorUtility.SetDirty(library);
                AssetDatabase.SaveAssets();
            }
        }

        internal static void EnsureFolder(string folder)
        {
            var parts = folder.Split('/');
            var path = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = path + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(path, parts[i]);
                }

                path = next;
            }
        }

        private static void Report(string message, bool interactive)
        {
            Debug.LogError("[items] " + message);
            if (interactive)
            {
                EditorUtility.DisplayDialog("Item catalog", message, "OK");
            }
        }
    }

    /// <summary>Exports the catalog whenever an item definition is saved, moved or deleted.</summary>
    public sealed class ItemAssetWatcher : AssetPostprocessor
    {
        private static bool _scheduled;

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (_scheduled)
            {
                return;
            }

            // A deleted asset has no type any more, so judge those by folder.
            bool relevant = importedAssets.Concat(movedAssets).Any(ItemCatalogTools.IsItemAsset)
                || deletedAssets.Any(p => p.StartsWith(ItemCatalogTools.ItemsFolder + "/", StringComparison.Ordinal));
            if (!relevant)
            {
                return;
            }

            // Once per batch of changes, after Unity has finished importing them.
            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                ItemCatalogTools.Export(interactive: false);
            };
        }
    }
}
