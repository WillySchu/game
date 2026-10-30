using System.Collections.Generic;
using System.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Loot;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace RaidZero.Items.Editor
{
    /// <summary>
    /// The loot table inspector: the entries as a list (new ones start with sensible defaults instead of all
    /// zeroes), every problem LootRoller.Validate finds, and a Simulate button that rolls the table a thousand
    /// times into a container and shows how often each item turns up.
    /// </summary>
    [CustomEditor(typeof(LootTableAsset))]
    public sealed class LootTableAssetEditor : UnityEditor.Editor
    {
        private const int Runs = 1000;

        private ReorderableList _entries;
        private string[] _containers = new string[0];
        private int _container;
        private LootSimulation _simulation;
        private ItemCatalog _catalog;

        private void OnEnable()
        {
            _entries = new ReorderableList(serializedObject, serializedObject.FindProperty("_entries"), true, true, true, true)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Entries (one is picked per roll, by weight)"),
                drawElementCallback = DrawEntry,
                elementHeightCallback = index => EditorGUI.GetPropertyHeight(_entries.serializedProperty.GetArrayElementAtIndex(index), GUIContent.none, true) + 4,
                onAddCallback = AddEntry,
            };

            _catalog = ItemCatalogTools.TryLoadCurrent();
            var containers = _catalog == null ? new List<ItemDefinition>() : _catalog.All.Where(d => d.HasGrids()).OrderBy(d => d.DefId).ToList();
            _containers = containers.Select(d => d.DefId).ToArray();
            _container = DefaultContainer(containers);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_minRolls"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_maxRolls"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_nothingWeight"));
            EditorGUILayout.Space();
            _entries.DoLayoutList();
            serializedObject.ApplyModifiedProperties();

            var asset = (LootTableAsset)target;
            if (_catalog == null)
            {
                EditorGUILayout.HelpBox("Couldn't read shared/catalog/item_catalog.json, so the table can't be checked. Save an item to export it.", MessageType.Warning);
                return;
            }

            var problems = LootRoller.Validate(_catalog, asset.ToTable());
            if (problems.Count == 0)
            {
                EditorGUILayout.HelpBox("No problems.", MessageType.Info);
            }

            foreach (var problem in problems)
            {
                EditorGUILayout.HelpBox(problem, MessageType.Error);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Simulate", EditorStyles.boldLabel);
            if (_containers.Length > 0)
            {
                _container = EditorGUILayout.Popup("Into a", _container, _containers);
            }

            if (GUILayout.Button("Roll it " + Runs.ToString("N0") + " times"))
            {
                var container = _containers.Length > 0 ? _containers[_container] : null;
                _simulation = LootSimulation.Run(_catalog, asset.ToTable(), container, Runs, (ulong)System.DateTime.Now.Ticks);
            }

            if (_simulation != null)
            {
                DrawSimulation(_simulation);
            }
        }

        private void DrawEntry(Rect rect, int index, bool isActive, bool isFocused)
        {
            var entry = _entries.serializedProperty.GetArrayElementAtIndex(index);
            rect.y += 2;
            rect.x += 10; // room for the foldout arrow beside the drag handle
            rect.width -= 10;
            EditorGUI.PropertyField(rect, entry, new GUIContent(Label(entry)), true);
        }

        // "3 × AK Rifle", "1 × table: loot_ammo", "(empty)"
        private static string Label(SerializedProperty entry)
        {
            var weight = entry.FindPropertyRelative("Weight").intValue;
            var item = entry.FindPropertyRelative("Item").objectReferenceValue as ItemDefinitionAsset;
            var table = entry.FindPropertyRelative("Table").objectReferenceValue as LootTableAsset;
            var what = item != null ? item.DisplayName : table != null ? "table: " + table.name : "(empty)";
            return "weight " + weight + ": " + what;
        }

        private static void AddEntry(ReorderableList list)
        {
            var entries = list.serializedProperty;
            int index = entries.arraySize;
            entries.arraySize = index + 1;
            var entry = entries.GetArrayElementAtIndex(index);
            entry.FindPropertyRelative("Weight").intValue = 1;
            entry.FindPropertyRelative("Item").objectReferenceValue = null;
            entry.FindPropertyRelative("Table").objectReferenceValue = null;
            entry.FindPropertyRelative("MinQuantity").intValue = 1;
            entry.FindPropertyRelative("MaxQuantity").intValue = 1;
            entry.FindPropertyRelative("MinCondition").floatValue = 1;
            entry.FindPropertyRelative("MaxCondition").floatValue = 1;
            entry.FindPropertyRelative("LoadedChance").floatValue = 0;
            entry.FindPropertyRelative("MinFill").floatValue = 1;
            entry.FindPropertyRelative("MaxFill").floatValue = 1;
            entry.isExpanded = true;
            list.index = index;
        }

        private static void DrawSimulation(LootSimulation sim)
        {
            EditorGUILayout.LabelField("Items per container", sim.AverageItems.ToString("0.00"));
            EditorGUILayout.LabelField("Empty", sim.EmptyChance.ToString("P0"));
            if (sim.AverageLeftOut > 0)
            {
                EditorGUILayout.HelpBox("On average " + sim.AverageLeftOut.ToString("0.00") + " items per container didn't fit and were left out. "
                    + "The table may be too generous for that container.", MessageType.Warning);
            }

            EditorGUILayout.LabelField("Item", "In this many  ·  average stack");
            foreach (var odds in sim.Items)
            {
                var name = ItemLibrary.DisplayNameOf(odds.DefId);
                var stack = odds.AverageQuantity > 1 ? "  ·  " + odds.AverageQuantity.ToString("0") : "";
                EditorGUILayout.LabelField(name, odds.Chance.ToString("P1") + stack);
            }
        }

        // The container type this table is the default for, if any; otherwise a wooden crate; otherwise the first.
        private int DefaultContainer(List<ItemDefinition> containers)
        {
            var library = ItemLibrary.Instance;
            for (int i = 0; i < containers.Count; i++)
            {
                var asset = library != null ? library.Find(containers[i].DefId) : null;
                if (asset != null && asset.DefaultLoot == target)
                {
                    return i;
                }
            }

            var crate = containers.FindIndex(d => d.DefId == "crate_wooden");
            return crate >= 0 ? crate : 0;
        }
    }
}
