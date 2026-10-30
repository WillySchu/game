using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RaidZero.Items.Editor
{
    /// <summary>
    /// Draws inventory icons from items' world models: Raid Zero > Items > Render Icons for every item that has
    /// one, or Render Icon from an item asset's ⋮ menu for just that item.
    ///
    /// Each icon is the item's footprint at <see cref="PixelsPerCell"/> pixels per cell (a 4x2 rifle is 256x128),
    /// looking at the model's side along +Z with +X to the right; an item's Icon Angles turn a model that faces
    /// another way. The model is lit in an empty preview scene and drawn twice, over black and over white, and
    /// the difference between the two gives each pixel's transparency, so this works whatever the render
    /// pipeline does with alpha. The PNGs go in <see cref="Folder"/>, named after the def_id, and each item's
    /// Rendered Icon points at its own. A hand-made Icon on the asset still wins over the rendered one.
    /// </summary>
    public static class ItemIconRenderer
    {
        public const int PixelsPerCell = 64;
        public const string Folder = "Assets/_Project/Data/ItemIcons";

        // Space left around the model, as a fraction of the icon.
        private const float Padding = 0.08f;

        // Each shot is drawn this many times larger and shrunk back down, which smooths the edges whatever
        // anti-aliasing the pipeline does.
        private const int Supersample = 4;

        [MenuItem("Raid Zero/Items/Render Icons")]
        private static void RenderAllFromMenu()
        {
            var rendered = 0;
            var problems = new List<string>();
            foreach (var item in ItemCatalogTools.FindAll())
            {
                if (item.WorldPrefab == null)
                {
                    continue;
                }

                if (Render(item, out var problem))
                {
                    rendered++;
                }
                else
                {
                    problems.Add(item.DefId + ": " + problem);
                }
            }

            AssetDatabase.SaveAssets();
            var message = rendered + " icon(s) rendered. Items without a world model keep the plain look.";
            if (problems.Count > 0)
            {
                message += "\n\nNot rendered:\n" + string.Join("\n", problems);
            }

            Debug.Log("[items] " + message);
            EditorUtility.DisplayDialog("Render Icons", message, "OK");
        }

        [MenuItem("CONTEXT/ItemDefinitionAsset/Render Icon")]
        private static void RenderOneFromMenu(MenuCommand command)
        {
            var item = (ItemDefinitionAsset)command.context;
            if (Render(item, out var problem))
            {
                AssetDatabase.SaveAssets();
                Debug.Log("[items] Rendered the icon for " + item.DefId + ".");
            }
            else
            {
                EditorUtility.DisplayDialog("Render Icon", item.DefId + ": " + problem, "OK");
            }
        }

        /// <summary>Renders the item's icon, saves it and points the item at it. False, with why, if it can't.</summary>
        public static bool Render(ItemDefinitionAsset item, out string problem)
        {
            problem = null;
            if (item.WorldPrefab == null)
            {
                problem = "it has no world model.";
                return false;
            }

            if (string.IsNullOrEmpty(item.DefId))
            {
                problem = "it has no def_id to name the icon after.";
                return false;
            }

            var width = Mathf.Max(1, item.Size.x) * PixelsPerCell;
            var height = Mathf.Max(1, item.Size.y) * PixelsPerCell;
            Texture2D overBlack = null;
            Texture2D overWhite = null;
            Texture2D icon = null;
            var preview = new PreviewRenderUtility();
            try
            {
                var model = preview.InstantiatePrefabInScene(item.WorldPrefab);
                model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(item.IconAngles));
                if (!TryGetBounds(model, out var bounds))
                {
                    problem = "its world model has nothing to draw (no renderers).";
                    return false;
                }

                Frame(preview, bounds, (float)width / height);
                overBlack = Shoot(preview, width * Supersample, height * Supersample, Color.black);
                overWhite = Shoot(preview, width * Supersample, height * Supersample, Color.white);
                icon = Combine(overBlack, overWhite, width, height);

                var path = Folder + "/" + item.DefId + ".png";
                ItemCatalogTools.EnsureFolder(Folder);
                File.WriteAllBytes(path, icon.EncodeToPNG());
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var sprite = ImportAsSprite(path);
                if (sprite == null)
                {
                    problem = "Unity didn't import " + path + " as a sprite.";
                    return false;
                }

                var serialized = new SerializedObject(item);
                serialized.FindProperty("_renderedIcon").objectReferenceValue = sprite;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(item);
                return true;
            }
            finally
            {
                preview.Cleanup();
                Object.DestroyImmediate(overBlack);
                Object.DestroyImmediate(overWhite);
                Object.DestroyImmediate(icon);
            }
        }

        private static bool TryGetBounds(GameObject model, out Bounds bounds)
        {
            bounds = default;
            var found = false;
            foreach (var renderer in model.GetComponentsInChildren<Renderer>())
            {
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return found;
        }

        // An orthographic camera on the model's -Z side, sized so the whole model fits the icon's shape.
        private static void Frame(PreviewRenderUtility preview, Bounds bounds, float aspect)
        {
            var camera = preview.camera;
            camera.orthographic = true;
            camera.aspect = aspect;
            camera.orthographicSize = Mathf.Max(bounds.extents.y, bounds.extents.x / aspect) * (1f + Padding * 2f);
            camera.transform.rotation = Quaternion.identity;
            camera.transform.position = bounds.center - Vector3.forward * (bounds.extents.z + 1f);
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = bounds.size.z + 2f;
            camera.clearFlags = CameraClearFlags.SolidColor;

            // A key light from above and to the left, a dimmer fill from the other side, and some ambient so
            // shadowed sides stay readable on the dark inventory background.
            var lights = preview.lights;
            lights[0].intensity = 1.1f;
            lights[0].transform.rotation = Quaternion.Euler(40f, 30f, 0f);
            lights[1].intensity = 0.6f;
            lights[1].transform.rotation = Quaternion.Euler(20f, 210f, 0f);
            preview.ambientColor = new Color(0.35f, 0.35f, 0.37f);
        }

        private static Texture2D Shoot(PreviewRenderUtility preview, int width, int height, Color background)
        {
            var camera = preview.camera;
            var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                camera.backgroundColor = background;
                preview.Render(true, false);

                RenderTexture.active = target;
                var shot = new Texture2D(width, height, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                shot.Apply();
                return shot;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        // Shrinks both shots to the icon's size, then works out each pixel's colour and transparency from the
        // pair. The arithmetic is done on linear colour, the way the GPU blended the edges, when the project
        // renders in linear space.
        private static Texture2D Combine(Texture2D overBlack, Texture2D overWhite, int width, int height)
        {
            var linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var black = ItemIconMath.Downsample(ToWorkingSpace(overBlack.GetPixels(), linear), overBlack.width, overBlack.height, Supersample);
            var white = ItemIconMath.Downsample(ToWorkingSpace(overWhite.GetPixels(), linear), overWhite.width, overWhite.height, Supersample);
            var result = new Color[width * height];
            for (int i = 0; i < result.Length; i++)
            {
                var pixel = ItemIconMath.Unblend(black[i], white[i]);
                if (linear)
                {
                    var alpha = pixel.a;
                    pixel = pixel.gamma;
                    pixel.a = alpha;
                }

                result[i] = pixel;
            }

            var icon = new Texture2D(width, height, TextureFormat.RGBA32, false);
            icon.SetPixels(result);
            icon.Apply();
            return icon;
        }

        private static Color[] ToWorkingSpace(Color[] pixels, bool linear)
        {
            if (linear)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = pixels[i].linear;
                }
            }

            return pixels;
        }

        private static Sprite ImportAsSprite(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                return null;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }
    }

    /// <summary>The pixel arithmetic behind <see cref="ItemIconRenderer"/>.</summary>
    public static class ItemIconMath
    {
        /// <summary>Averages each factor-by-factor block of pixels (rows bottom to top, as Unity stores them) into one.</summary>
        public static Color[] Downsample(Color[] pixels, int width, int height, int factor)
        {
            var outWidth = width / factor;
            var outHeight = height / factor;
            var result = new Color[outWidth * outHeight];
            var weight = 1f / (factor * factor);
            for (int y = 0; y < outHeight; y++)
            {
                for (int x = 0; x < outWidth; x++)
                {
                    var sum = new Color(0f, 0f, 0f, 0f);
                    for (int dy = 0; dy < factor; dy++)
                    {
                        var row = (y * factor + dy) * width + x * factor;
                        for (int dx = 0; dx < factor; dx++)
                        {
                            sum += pixels[row + dx];
                        }
                    }

                    result[y * outWidth + x] = sum * weight;
                }
            }

            return result;
        }

        /// <summary>A pixel's colour and transparency from the same pixel drawn over black and over white.</summary>
        public static Color Unblend(Color overBlack, Color overWhite)
        {
            var showThrough = ((overWhite.r - overBlack.r) + (overWhite.g - overBlack.g) + (overWhite.b - overBlack.b)) / 3f;
            var alpha = Mathf.Clamp01(1f - showThrough);
            if (alpha < 1f / 255f)
            {
                return new Color(0f, 0f, 0f, 0f);
            }

            return new Color(
                Mathf.Clamp01(overBlack.r / alpha),
                Mathf.Clamp01(overBlack.g / alpha),
                Mathf.Clamp01(overBlack.b / alpha),
                alpha);
        }
    }
}
