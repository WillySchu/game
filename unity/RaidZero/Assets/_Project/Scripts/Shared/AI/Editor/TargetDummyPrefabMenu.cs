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
    /// capsule's collider is the torso, and a sphere on top, with its own collider, is the head.
    /// </summary>
    public static class TargetDummyPrefabMenu
    {
        public const string PrefabPath = "Assets/_Project/Prefabs/" + RaidServer.TargetDummyPrefab + ".prefab";
        public const string MaterialPath = "Assets/_Project/Materials/TargetDummy.mat";

        [MenuItem("Raid Zero/AI/Create Target Dummy Prefab")]
        public static void Create()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null
                && !EditorUtility.DisplayDialog("Target dummy", PrefabPath + " already exists. Replace it?", "Replace", "Cancel"))
            {
                return;
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

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log("[ai] Saved " + PrefabPath + ". Check it's in DefaultPrefabObjects, then commit both.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // A plain orange, so it's easy to tell from a player's capsule.
        private static Material DummyMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (existing != null)
            {
                return existing;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = new Material(shader) { name = "TargetDummy" };
            material.SetColor("_BaseColor", new Color(0.95f, 0.55f, 0.15f));
            material.SetColor("_Color", new Color(0.95f, 0.55f, 0.15f));
            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }
    }
}
