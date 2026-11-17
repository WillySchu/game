using FishNet.Component.Transforming;
using FishNet.Object;
using RaidZero.Net;
using UnityEditor;
using UnityEngine;
using BodyPart = RaidZero.Shared.Raid.BodyPart; // UnityEditor has a BodyPart too (for avatars)

namespace RaidZero.Shared.Editor
{
    /// <summary>
    /// Raid Zero > AI > Create Target Dummy Prefab: builds the TargetDummy prefab the F1 overlay's Spawn target
    /// dummy button uses. Run it once and commit the prefab (Fish-Net adds it to the spawnable prefabs, the
    /// DefaultPrefabObjects asset, by itself; commit that change too). An orange capsule, 1.8 m tall: the
    /// capsule's collider is the torso, and a sphere on top, with its own collider, is the head; a dark visor
    /// shows which way it faces. It sees and hears (<see cref="AIPerceiver"/>) and turns to look, which reaches
    /// clients through its NetworkTransform. Run on a prefab from before any of that, Update adds what's missing
    /// and keeps the prefab (and its place in DefaultPrefabObjects) as it is.
    /// </summary>
    public static class TargetDummyPrefabMenu
    {
        public const string PrefabPath = "Assets/_Project/Prefabs/" + RaidServer.TargetDummyPrefab + ".prefab";
        public const string MaterialPath = "Assets/_Project/Materials/TargetDummy.mat";
        public const string VisorMaterialPath = "Assets/_Project/Materials/TargetDummyVisor.mat";

        [MenuItem("Raid Zero/AI/Create Target Dummy Prefab")]
        public static void Create()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            {
                // 0 Update, 1 Cancel, 2 Replace.
                var choice = EditorUtility.DisplayDialogComplex("Target dummy",
                    PrefabPath + " already exists. Update adds anything it's missing (its eyes and ears, a NetworkTransform, a visor) and keeps the rest.",
                    "Update", "Cancel", "Replace");
                if (choice == 0)
                {
                    UpdateExisting();
                    return;
                }

                if (choice == 1)
                {
                    return;
                }
            }

            var material = DummyMaterial();
            var root = new GameObject(RaidServer.TargetDummyPrefab);
            try
            {
                // The torso: the root's own collider, from the feet to the shoulders.
                var torso = root.AddComponent<CapsuleCollider>();
                torso.center = new Vector3(0f, 0.75f, 0f);
                torso.height = 1.5f;
                torso.radius = 0.35f;
                root.AddComponent<TargetDummy>();
                root.AddComponent<NetworkObject>();

                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                Object.DestroyImmediate(body.GetComponent<Collider>());
                body.transform.SetParent(root.transform, false);
                body.transform.localPosition = new Vector3(0f, 0.75f, 0f);
                body.transform.localScale = new Vector3(0.7f, 0.75f, 0.7f);
                body.GetComponent<MeshRenderer>().sharedMaterial = material;

                // The head: a sphere with its own collider and hitbox.
                var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                head.name = "Head";
                head.transform.SetParent(root.transform, false);
                head.transform.localPosition = new Vector3(0f, 1.62f, 0f);
                head.transform.localScale = Vector3.one * 0.3f;
                head.GetComponent<MeshRenderer>().sharedMaterial = material;
                head.AddComponent<TargetDummyHitbox>().Part = BodyPart.Head;

                AddWhatsMissing(root);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log("[ai] Saved " + PrefabPath + ". Check it's in DefaultPrefabObjects, then commit both.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // The existing prefab, with what later steps added to the dummy.
        private static void UpdateExisting()
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var added = AddWhatsMissing(root);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log("[ai] Updated " + PrefabPath + (added.Length > 0 ? ": added " + added + "." : ": it had everything already.") + " Commit it.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // Its eyes and ears, a NetworkTransform so its turning reaches clients, and a visor to see it turn.
        private static string AddWhatsMissing(GameObject root)
        {
            var added = new System.Collections.Generic.List<string>();
            if (root.GetComponent<AIPerceiver>() == null)
            {
                root.AddComponent<AIPerceiver>();
                added.Add("an AIPerceiver");
            }

            if (root.GetComponent<NetworkTransform>() == null)
            {
                var networkTransform = root.AddComponent<NetworkTransform>();
                networkTransform.SetSynchronizeScale(false);
                added.Add("a NetworkTransform");
            }

            if (root.transform.Find("Visor") == null)
            {
                var visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visor.name = "Visor";
                Object.DestroyImmediate(visor.GetComponent<Collider>());
                visor.transform.SetParent(root.transform, false);
                visor.transform.localPosition = new Vector3(0f, 1.64f, 0.13f);
                visor.transform.localScale = new Vector3(0.22f, 0.07f, 0.06f);
                visor.GetComponent<MeshRenderer>().sharedMaterial = VisorMaterial();
                added.Add("a visor");
            }

            return string.Join(", ", added);
        }

        private static Material VisorMaterial() => MaterialAt(VisorMaterialPath, "TargetDummyVisor", new Color(0.08f, 0.08f, 0.1f));

        // A plain orange, so it's easy to tell from a player's capsule.
        private static Material DummyMaterial() => MaterialAt(MaterialPath, "TargetDummy", new Color(0.95f, 0.55f, 0.15f));

        private static Material MaterialAt(string path, string name, Color color)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                return existing;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = new Material(shader) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetColor("_Color", color);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
