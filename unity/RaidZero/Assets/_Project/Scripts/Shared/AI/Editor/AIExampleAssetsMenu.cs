using System.Collections.Generic;
using System.Linq;
using FishNet.Object;
using RaidZero.Items;
using UnityEditor;
using UnityEngine;
using Faction = RaidZero.Shared.Raid.Faction;

namespace RaidZero.Shared.Editor
{
    /// <summary>
    /// Raid Zero > AI > Create Example Profiles: a looter and a contractor profile, each with one kit, in
    /// Data/AI, to put on spawn zones and tune. Both spawn the target dummy until the AI body exists. Looters
    /// carry a pistol, two spare magazines, a rig and a medkit; contractors a rifle, two spares, the light vest and
    /// a rig. Run it once; it leaves existing assets alone.
    /// </summary>
    public static class AIExampleAssetsMenu
    {
        public const string Folder = "Assets/_Project/Data/AI";

        [MenuItem("Raid Zero/AI/Create Example Profiles")]
        public static void Create()
        {
            var dummy = AssetDatabase.LoadAssetAtPath<GameObject>(TargetDummyPrefabMenu.PrefabPath);
            var prefab = dummy != null ? dummy.GetComponent<NetworkObject>() : null;
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("AI profiles", "Make the target dummy first: Raid Zero > AI > Create Target Dummy Prefab.", "OK");
                return;
            }

            var items = new Dictionary<string, ItemDefinitionAsset>();
            foreach (var guid in AssetDatabase.FindAssets("t:ItemDefinitionAsset"))
            {
                var item = AssetDatabase.LoadAssetAtPath<ItemDefinitionAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (item != null && !string.IsNullOrEmpty(item.DefId))
                {
                    items[item.DefId] = item;
                }
            }

            ItemDefinitionAsset Item(string defId)
            {
                if (!items.TryGetValue(defId, out var item))
                {
                    Debug.LogWarning("[ai] No item asset for " + defId + "; the example kit leaves it out.");
                }

                return item;
            }

            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets/_Project/Data", "AI");
            }

            var looterKit = Kit("Kit_Looter_Pistol", kit => kit.Set(Item("pistol_pm"), Item("mag_pm_8"), Item("ammo_9x18_pst"), 2,
                null, Item("rig_chest_basic"), null, new[] { Item("medkit_basic") }.Where(i => i != null)));
            var contractorKit = Kit("Kit_Contractor_Rifle", kit => kit.Set(Item("rifle_ak_basic"), Item("mag_ak_30"), Item("ammo_545_ps"), 2,
                Item("armor_vest_light"), Item("rig_chest_basic"), null, new ItemDefinitionAsset[0]));
            Profile("AIProfile_Looters", profile => profile.Set(Faction.Looters, prefab, new[] { looterKit }, null));
            Profile("AIProfile_Contractors", profile => profile.Set(Faction.Contractors, prefab, new[] { contractorKit }, null));
            AssetDatabase.SaveAssets();
            Debug.Log("[ai] Example profiles and kits are in " + Folder + ".");
        }

        private static AIKitAsset Kit(string name, System.Action<AIKitAsset> fill) => Make(name, fill);

        private static AIProfile Profile(string name, System.Action<AIProfile> fill) => Make(name, fill);

        // An asset in Folder, left as it is if it's there already.
        private static T Make<T>(string name, System.Action<T> fill) where T : ScriptableObject
        {
            var path = Folder + "/" + name + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
            {
                return existing;
            }

            var asset = ScriptableObject.CreateInstance<T>();
            fill(asset);
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }
    }
}
