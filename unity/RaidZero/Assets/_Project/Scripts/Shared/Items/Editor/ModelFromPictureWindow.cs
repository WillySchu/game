using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace RaidZero.Items.Editor
{
    /// <summary>
    /// Raid Zero > Items > Model From Picture: makes a quick model of an item from a side-on picture of it, the
    /// way you'd make one by tracing and extruding by hand (<see cref="PictureModelBuilder"/> does the work).
    /// Also in an item asset's ⋮ menu, which fills the item in.
    ///
    /// It writes a mesh, the picture as its texture, a material and a prefab into
    /// <see cref="OutputRoot"/>/&lt;name&gt;. With an item chosen, the prefab becomes the item's world model and
    /// its icon is rendered. Making it again with the same name updates those files in place, so anything that
    /// uses the prefab picks up the new model.
    /// </summary>
    public sealed class ModelFromPictureWindow : EditorWindow
    {
        public const string OutputRoot = "Assets/_Project/Models/FromPictures";

        private static readonly Color PreviewBackground = new Color(0.22f, 0.22f, 0.24f);

        [SerializeField] private Texture2D _picture;
        [SerializeField] private ItemDefinitionAsset _item;
        [SerializeField] private string _name = "";
        [SerializeField] private float _length = 0.9f;
        [SerializeField] private float _thickness = 0.05f;
        [SerializeField] private Texture2D _thicknessMap;
        [SerializeField] private float _thinnest = 0.4f;
        [SerializeField] private int _steps = 3;
        [SerializeField] private PictureBackground _background = PictureBackground.Auto;
        [SerializeField] private float _tolerance = 0.12f;
        [SerializeField] private bool _cutHoles = true;
        [SerializeField] private float _detail = 1f;

        private PictureModel _model;
        private Texture2D _cutout;
        private string _problem;
        private bool _stale = true;
        private Vector2 _scroll;

        [MenuItem("Raid Zero/Items/Model From Picture...")]
        private static void OpenFromMenu()
        {
            var window = Open();
            if (Selection.activeObject is Texture2D picture)
            {
                window.SetPicture(picture);
            }
            else if (Selection.activeObject is ItemDefinitionAsset item)
            {
                window.SetItem(item);
            }
        }

        [MenuItem("CONTEXT/ItemDefinitionAsset/Model From Picture...")]
        private static void OpenForItem(MenuCommand command) => Open().SetItem((ItemDefinitionAsset)command.context);

        private static ModelFromPictureWindow Open()
        {
            var window = GetWindow<ModelFromPictureWindow>("Model From Picture");
            window.minSize = new Vector2(380f, 520f);
            return window;
        }

        private void SetItem(ItemDefinitionAsset item)
        {
            _item = item;
            if (item != null && !string.IsNullOrEmpty(item.DefId))
            {
                _name = item.DefId;
            }

            Repaint();
        }

        private void SetPicture(Texture2D picture)
        {
            _picture = picture;
            if (string.IsNullOrEmpty(_name) && picture != null)
            {
                _name = picture.name;
            }

            _stale = true;
            Repaint();
        }

        private void OnDisable() => ClearPreview();

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.HelpBox(
                "A side view of the item, lengthwise left to right, on a transparent or plain background. "
                + "It's cut out, traced, filled and extruded, and the picture is painted on both sides.",
                MessageType.None);

            EditorGUI.BeginChangeCheck();
            var picture = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("Picture", "A PNG or JPG works best: those are read straight from the file, whatever the texture's import settings."),
                _picture, typeof(Texture2D), false);
            if (EditorGUI.EndChangeCheck())
            {
                SetPicture(picture);
            }

            EditorGUI.BeginChangeCheck();
            var item = (ItemDefinitionAsset)EditorGUILayout.ObjectField(
                new GUIContent("Item (optional)", "Gets the model as its world model, and its icon rendered."),
                _item, typeof(ItemDefinitionAsset), false);
            if (EditorGUI.EndChangeCheck())
            {
                SetItem(item);
            }

            _name = EditorGUILayout.TextField(new GUIContent("Name", "The files are named after it, in " + OutputRoot + "/<name>."), _name);

            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            _length = EditorGUILayout.FloatField(new GUIContent("Length (m)", "The item's real length, end to end. An AR-15 is about 0.9 m, a hunting rifle 1.1 m, a pistol 0.2 m."), _length);
            _thickness = EditorGUILayout.FloatField(new GUIContent("Thickness (m)", "How thick it is. A rifle's receiver is about 0.05 m."), _thickness);
            _thicknessMap = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("Thickness map (optional)", "A grey picture lined up with the picture: white parts at full thickness, black at Thinnest, grey between. Paint a copy of the picture."),
                _thicknessMap, typeof(Texture2D), false);
            using (new EditorGUI.DisabledScope(_thicknessMap == null))
            {
                _thinnest = EditorGUILayout.Slider(new GUIContent("Thinnest", "How thick the map's black parts are, as a share of Thickness."), _thinnest, 0.05f, 1f);
                _steps = EditorGUILayout.IntSlider(new GUIContent("Steps", "How many thicknesses the map is rounded to."), _steps, 2, 6);
            }

            EditorGUILayout.Space();
            _background = (PictureBackground)EditorGUILayout.EnumPopup(new GUIContent("Background", "Auto uses transparency if the picture has any, otherwise the colour in its corners."), _background);
            using (new EditorGUI.DisabledScope(_background == PictureBackground.Transparency))
            {
                _tolerance = EditorGUILayout.Slider(new GUIContent("Colour tolerance", "How different from the background a colour must be to count as item. Raise it if background is left on; lower it if parts of the item go missing."), _tolerance, 0.01f, 0.6f);
            }

            _cutHoles = EditorGUILayout.Toggle(new GUIContent("Cut enclosed holes", "Cut out background the item surrounds, like the inside of a trigger guard. Turn off if pale parts of the item are being cut out."), _cutHoles);
            _detail = EditorGUILayout.Slider(new GUIContent("Outline detail", "How far the outline may stray from the picture's edge, in pixels. Bigger is simpler, with fewer triangles."), _detail, 0.25f, 6f);
            if (EditorGUI.EndChangeCheck())
            {
                _stale = true;
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_picture == null))
                {
                    if (GUILayout.Button("Preview"))
                    {
                        // On the next layout pass: changing what's drawn in the middle of a click upsets the layout.
                        _stale = true;
                        Repaint();
                    }

                    if (GUILayout.Button(_item != null ? "Make Model and Use It" : "Make Model"))
                    {
                        Make();
                        GUIUtility.ExitGUI();
                    }
                }
            }

            if (_picture != null && _stale && Event.current.type == EventType.Layout)
            {
                Preview();
            }

            DrawPreview();
            EditorGUILayout.EndScrollView();
        }

        private void Preview()
        {
            _stale = false;
            _problem = null;
            ClearPreview();
            if (_picture == null)
            {
                return;
            }

            try
            {
                var pixels = ReadPixels(_picture);
                _model = PictureModelBuilder.Build(pixels, _thicknessMap != null ? ReadPixels(_thicknessMap) : null, Settings());
                _cutout = CutoutTexture(pixels, _model.Coverage);
            }
            catch (PictureModelException e)
            {
                _problem = e.Message;
            }
            catch (Exception e)
            {
                _problem = "Couldn't make a model from the picture: " + e.Message;
                Debug.LogException(e);
            }
        }

        private PictureModelSettings Settings() => new PictureModelSettings
        {
            Background = _background,
            ColourTolerance = _tolerance,
            CutEnclosedBackground = _cutHoles,
            Length = _length,
            Thickness = _thickness,
            Thinnest = _thinnest,
            Steps = _steps,
            Detail = _detail,
        };

        private void DrawPreview()
        {
            if (_problem != null)
            {
                EditorGUILayout.HelpBox(_problem, MessageType.Warning);
                return;
            }

            if (_model == null || _cutout == null)
            {
                return;
            }

            int points = 0;
            foreach (var outline in _model.Outlines)
            {
                points += outline.Length / 2;
            }

            EditorGUILayout.LabelField(
                (_model.UsedTransparency ? "Cut out by transparency. " : "Cut out by background colour. ")
                + _model.Outlines.Count + (_model.Outlines.Count == 1 ? " outline, " : " outlines, ") + points + " points"
                + (_model.Layers > 1 ? ", " + _model.Layers + " layers" : "") + ".",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(
                _model.TriangleCount + " triangles, " + _model.WidthMetres.ToString("0.00") + " × " + _model.HeightMetres.ToString("0.00")
                + " × " + _thickness.ToString("0.00") + " m.",
                EditorStyles.wordWrappedMiniLabel);

            float aspect = (float)_cutout.width / _cutout.height;
            var rect = GUILayoutUtility.GetAspectRect(aspect, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            EditorGUI.DrawRect(rect, PreviewBackground);
            GUI.DrawTexture(rect, _cutout, ScaleMode.StretchToFill, true);

            // The traced outline over the cutout. The picture's rows go up; the window's go down.
            Handles.color = new Color(1f, 0.45f, 0.1f);
            float sx = rect.width / _cutout.width;
            float sy = rect.height / _cutout.height;
            foreach (var outline in _model.Outlines)
            {
                int count = outline.Length / 2;
                var line = new Vector3[count + 1];
                for (int i = 0; i <= count; i++)
                {
                    int k = (i % count) * 2;
                    line[i] = new Vector3(rect.x + outline[k] * sx, rect.yMax - outline[k + 1] * sy, 0f);
                }

                Handles.DrawAAPolyLine(2f, line);
            }
        }

        private void Make()
        {
            Preview();
            if (_model == null)
            {
                EditorUtility.DisplayDialog("Model From Picture", _problem ?? "Choose a picture first.", "OK");
                return;
            }

            var name = SafeName(string.IsNullOrWhiteSpace(_name) ? _picture.name : _name.Trim());
            var folder = OutputRoot + "/" + name;
            ItemCatalogTools.EnsureFolder(folder);

            var texture = SaveTexture(folder + "/" + name + "_picture.png", _model);
            var mesh = SaveMesh(folder + "/" + name + "_mesh.asset", name, _model);
            var material = SaveMaterial(folder + "/" + name + ".mat", texture);
            var prefab = SavePrefab(folder + "/" + name + ".prefab", name, mesh, material);
            AssetDatabase.SaveAssets();

            var message = "Made " + name + ": " + _model.TriangleCount + " triangles, in " + folder + ".";
            if (_item != null)
            {
                // The model is made the way the icon renderer looks at things, so it needs no turning.
                var serialized = new SerializedObject(_item);
                serialized.FindProperty("_worldPrefab").objectReferenceValue = prefab;
                serialized.FindProperty("_iconAngles").vector3Value = Vector3.zero;
                serialized.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                message += ItemIconRenderer.Render(_item, out var problem)
                    ? " It's " + _item.DefId + "'s world model now, and its icon is rendered."
                    : " It's " + _item.DefId + "'s world model now, but its icon didn't render: " + problem;
                AssetDatabase.SaveAssets();
            }

            Debug.Log("[items] " + message, prefab);
            EditorGUIUtility.PingObject(prefab);
        }

        // ---- Reading pictures ----

        /// <summary>
        /// The picture's pixels at its real size. PNGs and JPGs are read from the file, since the imported
        /// texture may be resized, compressed or unreadable; anything else is copied off the GPU.
        /// </summary>
        private static PicturePixels ReadPixels(Texture2D texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var copy = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if ((extension == ".png" || extension == ".jpg" || extension == ".jpeg") && copy.LoadImage(File.ReadAllBytes(path)))
                {
                    return ToPixels(copy.GetPixels32(), copy.width, copy.height);
                }

                var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var previous = RenderTexture.active;
                try
                {
                    Graphics.Blit(texture, target);
                    RenderTexture.active = target;
                    copy.Reinitialize(texture.width, texture.height);
                    copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                    copy.Apply();
                    return ToPixels(copy.GetPixels32(), copy.width, copy.height);
                }
                finally
                {
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(target);
                }
            }
            finally
            {
                DestroyImmediate(copy);
            }
        }

        private static PicturePixels ToPixels(Color32[] colours, int width, int height)
        {
            var rgba = new byte[colours.Length * 4];
            for (int i = 0; i < colours.Length; i++)
            {
                rgba[i * 4] = colours[i].r;
                rgba[i * 4 + 1] = colours[i].g;
                rgba[i * 4 + 2] = colours[i].b;
                rgba[i * 4 + 3] = colours[i].a;
            }

            return new PicturePixels(width, height, rgba);
        }

        private static Color32[] ToColours(byte[] rgba, float[] alpha)
        {
            var colours = new Color32[rgba.Length / 4];
            for (int i = 0; i < colours.Length; i++)
            {
                byte a = alpha != null ? (byte)(Mathf.Clamp01(alpha[i]) * 255f) : rgba[i * 4 + 3];
                colours[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], a);
            }

            return colours;
        }

        private static Texture2D CutoutTexture(PicturePixels pixels, float[] coverage)
        {
            var texture = new Texture2D(pixels.Width, pixels.Height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixels32(ToColours(pixels.Rgba, coverage));
            texture.Apply();
            return texture;
        }

        private void ClearPreview()
        {
            if (_cutout != null)
            {
                DestroyImmediate(_cutout);
            }

            _cutout = null;
            _model = null;
        }

        // ---- Writing assets ----

        private static Texture2D SaveTexture(string path, PictureModel model)
        {
            var texture = new Texture2D(model.TextureWidth, model.TextureHeight, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(ToColours(model.Texture, null));
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.alphaSource = TextureImporterAlphaSource.None;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.maxTextureSize = 2048;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Mesh SaveMesh(string path, string name, PictureModel model)
        {
            int count = model.VertexCount;
            var vertices = new List<Vector3>(count);
            var normals = new List<Vector3>(count);
            var uvs = new List<Vector2>(count);
            for (int i = 0; i < count; i++)
            {
                vertices.Add(new Vector3(model.Positions[i * 3], model.Positions[i * 3 + 1], model.Positions[i * 3 + 2]));
                normals.Add(new Vector3(model.Normals[i * 3], model.Normals[i * 3 + 1], model.Normals[i * 3 + 2]));
                uvs.Add(new Vector2(model.Uvs[i * 2], model.Uvs[i * 2 + 1]));
            }

            var mesh = new Mesh { name = name };
            mesh.indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(model.Triangles, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            // Update an existing mesh in place, so the prefab and anything else using it keep their reference.
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }

            existing.Clear();
            EditorUtility.CopySerialized(mesh, existing);
            existing.name = name;
            DestroyImmediate(mesh);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        private static Material SaveMaterial(string path, Texture2D texture)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                // Whatever the project renders with: URP's Lit, or the built-in Standard shader without a pipeline.
                var pipelineDefault = GraphicsSettings.defaultRenderPipeline != null ? GraphicsSettings.defaultRenderPipeline.defaultMaterial : null;
                material = pipelineDefault != null ? new Material(pipelineDefault) : new Material(Shader.Find("Standard"));
                if (material.HasProperty("_Smoothness"))
                {
                    material.SetFloat("_Smoothness", 0.25f);
                }

                AssetDatabase.CreateAsset(material, path);
            }

            material.mainTexture = texture;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static GameObject SavePrefab(string path, string name, Mesh mesh, Material material)
        {
            var root = new GameObject(name);
            try
            {
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                root.AddComponent<MeshRenderer>().sharedMaterial = material;
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                DestroyImmediate(root);
            }
        }

        private static string SafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name.Replace('/', '_').Replace('\\', '_');
        }
    }
}
