// Also compiled into tests/Game.Shared.Tests, which has nullable checks on; Unity's C# doesn't.
#nullable disable
using System;
using System.Collections.Generic;

namespace RaidZero.Items.Editor
{
    /// <summary>How the item is told apart from the rest of the picture.</summary>
    public enum PictureBackground
    {
        /// <summary>Transparency if the picture has any, otherwise the colour in its corners.</summary>
        Auto,

        /// <summary>The picture is already cut out: transparent pixels are background.</summary>
        Transparency,

        /// <summary>The background is one plain colour, taken from the picture's corners.</summary>
        Colour,
    }

    /// <summary>A picture's pixels: RGBA, four bytes each, rows from the bottom up (as Unity stores them).</summary>
    public sealed class PicturePixels
    {
        public PicturePixels(int width, int height, byte[] rgba)
        {
            if (width <= 0 || height <= 0 || rgba == null || rgba.Length != width * height * 4)
            {
                throw new ArgumentException("The pixels don't match the picture's size.");
            }

            Width = width;
            Height = height;
            Rgba = rgba;
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Rgba { get; }
    }

    public sealed class PictureModelSettings
    {
        public PictureBackground Background = PictureBackground.Auto;

        /// <summary>How far a pixel's colour can be from the background's and still count as background, 0 to 1.</summary>
        public float ColourTolerance = 0.12f;

        /// <summary>Cut out background the item encloses, such as the inside of a trigger guard. Off fills it in.</summary>
        public bool CutEnclosedBackground = true;

        /// <summary>The item's real length, end to end along the picture's width, in metres.</summary>
        public float Length = 1f;

        /// <summary>How thick the model is, in metres. With a thickness map, the thickness of its white parts.</summary>
        public float Thickness = 0.05f;

        /// <summary>With a thickness map, how thick its black parts are, as a fraction of Thickness.</summary>
        public float Thinnest = 0.4f;

        /// <summary>With a thickness map, how many thicknesses it's rounded to, from Thinnest to Thickness.</summary>
        public int Steps = 3;

        /// <summary>How far the outline may stray from the picture's edge, in working pixels. Bigger is simpler.</summary>
        public float Detail = 1f;

        /// <summary>The outline is traced on a copy of the picture at most this many pixels across.</summary>
        public int WorkingSize = 1024;

        /// <summary>Edges turning less than this, in degrees, are shaded smoothly; sharper ones stay sharp.</summary>
        public float SmoothingAngle = 40f;
    }

    /// <summary>The model made from a picture, ready to turn into a Unity mesh, and the texture to wear.</summary>
    public sealed class PictureModel
    {
        /// <summary>x, y, z per vertex, in metres, centred on the item.</summary>
        public float[] Positions;

        /// <summary>x, y, z per vertex.</summary>
        public float[] Normals;

        /// <summary>u, v per vertex, onto <see cref="Texture"/>.</summary>
        public float[] Uvs;

        /// <summary>Three vertex indices per triangle, clockwise seen from outside (Unity's front faces).</summary>
        public int[] Triangles;

        /// <summary>The picture with its background replaced by the nearest item colour, fully opaque, so nothing
        /// of the background shows at the model's edges. Same size as the picture, rows from the bottom up.</summary>
        public byte[] Texture;

        public int TextureWidth;
        public int TextureHeight;

        /// <summary>Which pixels are the item: 0 to 1 each, same size as the picture.</summary>
        public float[] Coverage;

        /// <summary>The item's outlines, in picture pixels, x then y per point: for previewing the trace.</summary>
        public List<float[]> Outlines = new List<float[]>();

        public bool UsedTransparency;
        public int Layers;
        public float WidthMetres;
        public float HeightMetres;

        public int VertexCount => Positions.Length / 3;
        public int TriangleCount => Triangles.Length / 3;
    }

    public sealed class PictureModelException : Exception
    {
        public PictureModelException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Makes a model from a side-on picture of an item, the way you'd make one by hand: cut the item out, trace
    /// its outline, fill it and extrude it. The picture goes on the front and back, and the colours along the
    /// edge are stretched over the sides. An optional thickness map (black thin, white thick) stacks layers of
    /// different thicknesses, so a magazine can be thinner than the receiver.
    ///
    /// The model lies in the X/Y plane, as the picture does (its width along +X, its height along +Y), with its
    /// thickness along Z and the picture's face toward -Z: the way <see cref="ItemIconRenderer"/> looks at an
    /// item. Plain C#, so it's tested outside Unity.
    /// </summary>
    public static class PictureModelBuilder
    {
        public static PictureModel Build(PicturePixels picture, PicturePixels thicknessMap, PictureModelSettings settings)
        {
            if (picture == null)
            {
                throw new ArgumentNullException(nameof(picture));
            }

            settings = settings ?? new PictureModelSettings();
            if (!(settings.Length > 0f) || !(settings.Thickness > 0f))
            {
                throw new PictureModelException("Length and thickness must be more than zero.");
            }

            int width = picture.Width;
            int height = picture.Height;
            var coverage = Cutout(picture, settings, out var usedTransparency);

            // The item's extent sets the scale and the pivot.
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (coverage[y * width + x] >= 0.5f)
                    {
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }

            if (maxX < 0)
            {
                throw new PictureModelException(usedTransparency
                    ? "The picture is all transparent: there's no item in it."
                    : "Couldn't tell the item from the background. Try a lower colour tolerance, or a picture with a plainer background.");
            }

            double metresPerPixel = settings.Length / (maxX - minX + 1);
            double centreX = (minX + maxX + 1) / 2.0;
            double centreY = (minY + maxY + 1) / 2.0;

            var layers = LayerCoverages(coverage, width, height, thicknessMap, settings, out var thicknesses);
            var mesh = new MeshBuilder();
            var model = new PictureModel
            {
                UsedTransparency = usedTransparency,
                Coverage = coverage,
                Texture = Bleed(picture, coverage),
                TextureWidth = width,
                TextureHeight = height,
                WidthMetres = (float)((maxX - minX + 1) * metresPerPixel),
                HeightMetres = (float)((maxY - minY + 1) * metresPerPixel),
            };

            int factor = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)Math.Max(16, settings.WorkingSize)));
            for (int layer = 0; layer < layers.Count; layer++)
            {
                var loops = TraceOutlines(layers[layer], width, height, factor, settings.Detail);
                if (layer == 0)
                {
                    foreach (var loop in loops)
                    {
                        var flat = new float[loop.Count * 2];
                        for (int i = 0; i < loop.Count; i++)
                        {
                            flat[i * 2] = (float)loop[i].X;
                            flat[i * 2 + 1] = (float)loop[i].Y;
                        }

                        model.Outlines.Add(flat);
                    }
                }

                if (loops.Count == 0)
                {
                    continue;
                }

                model.Layers++;
                var frame = new Frame
                {
                    MetresPerPixel = metresPerPixel,
                    CentreX = centreX,
                    CentreY = centreY,
                    Width = width,
                    Height = height,
                    HalfThickness = thicknesses[layer] / 2.0,
                    UvInset = Math.Max(1.5, factor * 0.75),
                    SmoothingCos = Math.Cos(settings.SmoothingAngle * Math.PI / 180.0),
                };
                Extrude(mesh, loops, frame);
            }

            if (mesh.TriangleCount == 0)
            {
                throw new PictureModelException("The item's outline came out empty. Try more detail (a smaller number).");
            }

            mesh.CopyTo(model);
            return model;
        }

        // ---- Cutting the item out ----

        /// <summary>Each pixel's share of item, 0 to 1. Small specks are dropped and small holes filled.</summary>
        internal static float[] Cutout(PicturePixels picture, PictureModelSettings settings, out bool usedTransparency)
        {
            int width = picture.Width;
            int height = picture.Height;
            int count = width * height;
            var rgba = picture.Rgba;
            var coverage = new float[count];

            var mode = settings.Background;
            if (mode == PictureBackground.Auto)
            {
                int clear = 0;
                for (int i = 0; i < count; i++)
                {
                    if (rgba[i * 4 + 3] < 128)
                    {
                        clear++;
                    }
                }

                mode = clear > count / 100 ? PictureBackground.Transparency : PictureBackground.Colour;
            }

            usedTransparency = mode == PictureBackground.Transparency;
            if (usedTransparency)
            {
                for (int i = 0; i < count; i++)
                {
                    coverage[i] = rgba[i * 4 + 3] / 255f;
                }
            }
            else
            {
                CornerColour(picture, out var br, out var bg, out var bb);
                double tolerance = Math.Max(0.0, settings.ColourTolerance) * 255.0 * Math.Sqrt(3.0);
                for (int i = 0; i < count; i++)
                {
                    double dr = rgba[i * 4] - br;
                    double dg = rgba[i * 4 + 1] - bg;
                    double db = rgba[i * 4 + 2] - bb;
                    coverage[i] = Math.Sqrt(dr * dr + dg * dg + db * db) > tolerance ? 1f : 0f;
                }
            }

            Clean(coverage, width, height, settings.CutEnclosedBackground);
            return coverage;
        }

        // The background's colour: the average of the picture's four corners, a few pixels each.
        private static void CornerColour(PicturePixels picture, out double r, out double g, out double b)
        {
            int size = Math.Max(1, Math.Min(4, Math.Min(picture.Width, picture.Height) / 8));
            double sr = 0, sg = 0, sb = 0;
            int n = 0;
            foreach (var (cx, cy) in new[] { (0, 0), (picture.Width - size, 0), (0, picture.Height - size), (picture.Width - size, picture.Height - size) })
            {
                for (int y = cy; y < cy + size; y++)
                {
                    for (int x = cx; x < cx + size; x++)
                    {
                        int i = (y * picture.Width + x) * 4;
                        sr += picture.Rgba[i];
                        sg += picture.Rgba[i + 1];
                        sb += picture.Rgba[i + 2];
                        n++;
                    }
                }
            }

            r = sr / n;
            g = sg / n;
            b = sb / n;
        }

        // Drops bits of item much smaller than the biggest (dust, a watermark), and fills holes in it: tiny ones
        // always (glare, a hole in the cutout), and every one when enclosed background isn't to be cut.
        private static void Clean(float[] coverage, int width, int height, bool cutHoles)
        {
            var label = new int[coverage.Length];
            var queue = new int[coverage.Length];
            var islands = new List<(int Label, int Size)>();
            var holes = new List<(int Label, int Size, bool Edge)>();
            int next = 0;
            int itemPixels = 0;
            for (int start = 0; start < coverage.Length; start++)
            {
                if (label[start] != 0)
                {
                    continue;
                }

                bool solid = coverage[start] >= 0.5f;
                next++;
                int size = Flood(coverage, width, height, start, solid, next, label, queue, out var edge);
                if (solid)
                {
                    islands.Add((next, size));
                    itemPixels += size;
                }
                else
                {
                    holes.Add((next, size, edge));
                }
            }

            int biggest = 0;
            foreach (var island in islands)
            {
                biggest = Math.Max(biggest, island.Size);
            }

            var drop = new HashSet<int>();
            foreach (var island in islands)
            {
                if (island.Size < Math.Max(8, biggest / 200))
                {
                    drop.Add(island.Label);
                }
            }

            int smallestHole = Math.Max(12, itemPixels / 1000);
            var fill = new HashSet<int>();
            foreach (var hole in holes)
            {
                if (!hole.Edge && (!cutHoles || hole.Size < smallestHole))
                {
                    fill.Add(hole.Label);
                }
            }

            for (int i = 0; i < coverage.Length; i++)
            {
                if (drop.Contains(label[i]))
                {
                    coverage[i] = 0f;
                }
                else if (fill.Contains(label[i]))
                {
                    coverage[i] = 1f;
                }
            }
        }

        // Labels the 4-connected region of pixels on the same side of half coverage as start.
        private static int Flood(float[] coverage, int width, int height, int start, bool solid, int id, int[] label, int[] queue, out bool touchesEdge)
        {
            int head = 0, tail = 0;
            queue[tail++] = start;
            label[start] = id;
            touchesEdge = false;
            while (head < tail)
            {
                int i = queue[head++];
                int x = i % width;
                int y = i / width;
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                {
                    touchesEdge = true;
                }

                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        continue;
                    }

                    int j = ny * width + nx;
                    if (label[j] == 0 && (coverage[j] >= 0.5f) == solid)
                    {
                        label[j] = id;
                        queue[tail++] = j;
                    }
                }
            }

            return tail;
        }

        /// <summary>
        /// The picture, opaque, with every pixel that isn't solidly item (the background, and the item's outermost
        /// pixel, which is usually part background) given the colour of the nearest one that is.
        /// </summary>
        internal static byte[] Bleed(PicturePixels picture, float[] coverage)
        {
            int width = picture.Width;
            int height = picture.Height;
            var texture = (byte[])picture.Rgba.Clone();
            var known = new bool[coverage.Length];
            var queue = new int[coverage.Length];
            int head = 0, tail = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (coverage[i] >= 0.99f
                        && (x == 0 || coverage[i - 1] >= 0.5f) && (x == width - 1 || coverage[i + 1] >= 0.5f)
                        && (y == 0 || coverage[i - width] >= 0.5f) && (y == height - 1 || coverage[i + width] >= 0.5f))
                    {
                        known[i] = true;
                        queue[tail++] = i;
                    }
                }
            }

            if (tail == 0)
            {
                // A sliver with no inside: use what there is.
                for (int i = 0; i < coverage.Length; i++)
                {
                    if (coverage[i] >= 0.5f)
                    {
                        known[i] = true;
                        queue[tail++] = i;
                    }
                }
            }

            while (head < tail)
            {
                int i = queue[head++];
                int x = i % width;
                int y = i / width;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        continue;
                    }

                    int j = ny * width + nx;
                    if (!known[j])
                    {
                        known[j] = true;
                        texture[j * 4] = texture[i * 4];
                        texture[j * 4 + 1] = texture[i * 4 + 1];
                        texture[j * 4 + 2] = texture[i * 4 + 2];
                        queue[tail++] = j;
                    }
                }
            }

            for (int i = 0; i < coverage.Length; i++)
            {
                texture[i * 4 + 3] = 255;
            }

            return texture;
        }

        // ---- Thickness layers ----

        // Layer 0 is the whole item at its thinnest; each layer above it is the part of the item at least that
        // thick, a little thicker. They overlap, so there's never a gap between a thin part and a thick one.
        private static List<float[]> LayerCoverages(float[] coverage, int width, int height, PicturePixels map, PictureModelSettings settings, out List<double> thicknesses)
        {
            var layers = new List<float[]>();
            thicknesses = new List<double>();
            int steps = Math.Max(1, Math.Min(8, settings.Steps));
            if (map == null || steps == 1)
            {
                layers.Add(coverage);
                thicknesses.Add(settings.Thickness);
                return layers;
            }

            double thinnest = Math.Max(0.05, Math.Min(1.0, settings.Thinnest));
            var level = new int[coverage.Length];
            for (int y = 0; y < height; y++)
            {
                int my = Math.Min(map.Height - 1, (int)((y + 0.5) * map.Height / height));
                for (int x = 0; x < width; x++)
                {
                    int mx = Math.Min(map.Width - 1, (int)((x + 0.5) * map.Width / width));
                    int m = (my * map.Width + mx) * 4;
                    double brightness = (map.Rgba[m] + map.Rgba[m + 1] + map.Rgba[m + 2]) / (3.0 * 255.0);
                    level[y * width + x] = (int)Math.Round(brightness * (steps - 1));
                }
            }

            for (int k = 0; k < steps; k++)
            {
                var layer = k == 0 ? coverage : new float[coverage.Length];
                bool any = k == 0;
                if (k > 0)
                {
                    for (int i = 0; i < coverage.Length; i++)
                    {
                        if (level[i] >= k)
                        {
                            layer[i] = coverage[i];
                            any |= coverage[i] >= 0.5f;
                        }
                    }
                }

                if (!any)
                {
                    continue;
                }

                double thickness = settings.Thickness * (thinnest + (1.0 - thinnest) * k / (steps - 1));
                if (layers.Count > 0 && SameShape(layers[layers.Count - 1], layer))
                {
                    // Nothing is exactly this thick: the layer below would only be hidden inside this one.
                    layers[layers.Count - 1] = layer;
                    thicknesses[thicknesses.Count - 1] = thickness;
                }
                else
                {
                    layers.Add(layer);
                    thicknesses.Add(thickness);
                }
            }

            return layers;
        }

        private static bool SameShape(float[] a, float[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if ((a[i] >= 0.5f) != (b[i] >= 0.5f))
                {
                    return false;
                }
            }

            return true;
        }

        // ---- Tracing ----

        internal struct P2
        {
            public P2(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X;
            public double Y;

            public static P2 operator -(P2 a, P2 b) => new P2(a.X - b.X, a.Y - b.Y);

            public static P2 operator +(P2 a, P2 b) => new P2(a.X + b.X, a.Y + b.Y);

            public static P2 operator *(P2 a, double s) => new P2(a.X * s, a.Y * s);

            public double Length => Math.Sqrt(X * X + Y * Y);

            public override string ToString() => "(" + X.ToString("0.###") + ", " + Y.ToString("0.###") + ")";
        }

        /// <summary>
        /// The outlines of where coverage is at least half, in picture pixels (0,0 the bottom-left corner of the
        /// bottom-left pixel), simplified. The item is on each outline's left: outer edges run anticlockwise and
        /// holes clockwise.
        /// </summary>
        internal static List<List<P2>> TraceOutlines(float[] coverage, int width, int height, int factor, double detail)
        {
            // Trace at a working size: each working pixel is the average of a factor-by-factor block.
            int w = (width + factor - 1) / factor;
            int h = (height + factor - 1) / factor;
            var grid = new float[w * h];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    grid[(y / factor) * w + x / factor] += coverage[y * width + x];
                }
            }

            float block = factor * factor;
            for (int i = 0; i < grid.Length; i++)
            {
                grid[i] /= block;
            }

            if (factor == 1)
            {
                grid = Blur(grid, w, h);
            }

            var loops = MarchingSquares(grid, w, h, 0.5f);
            var result = new List<List<P2>>();
            double minArea = 4.0;
            foreach (var loop in loops)
            {
                var simple = Simplify(loop, Math.Max(0.05, detail));
                if (simple.Count < 3 || Math.Abs(SignedArea(simple)) < minArea)
                {
                    continue;
                }

                // Working pixel centres to picture pixels.
                for (int i = 0; i < simple.Count; i++)
                {
                    simple[i] = new P2((simple[i].X + 0.5) * factor, (simple[i].Y + 0.5) * factor);
                }

                result.Add(simple);
            }

            return result;
        }

        // A light [1 2 1] blur, so outlines traced straight from a hard-edged mask come out smooth.
        private static float[] Blur(float[] grid, int w, int h)
        {
            var across = new float[grid.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float left = x > 0 ? grid[y * w + x - 1] : 0f;
                    float right = x < w - 1 ? grid[y * w + x + 1] : 0f;
                    across[y * w + x] = (left + 2f * grid[y * w + x] + right) / 4f;
                }
            }

            var result = new float[grid.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float down = y > 0 ? across[(y - 1) * w + x] : 0f;
                    float up = y < h - 1 ? across[(y + 1) * w + x] : 0f;
                    result[y * w + x] = (down + 2f * across[y * w + x] + up) / 4f;
                }
            }

            return result;
        }

        /// <summary>
        /// Closed outlines around values above iso, at sample positions (sample (x, y) at point (x, y)), with the
        /// inside on the left. Outside the grid counts as empty, so every outline closes.
        /// </summary>
        internal static List<List<P2>> MarchingSquares(float[] values, int width, int height, float iso)
        {
            int gw = width + 2;
            int gh = height + 2;

            float Value(int gx, int gy) =>
                gx >= 1 && gy >= 1 && gx <= width && gy <= height ? values[(gy - 1) * width + gx - 1] : 0f;

            int HorizontalId(int x, int y) => (y * gw + x) * 2;
            int VerticalId(int x, int y) => (y * gw + x) * 2 + 1;

            P2 Crossing(int id)
            {
                int cell = id / 2;
                int x = cell % gw;
                int y = cell / gw;
                int x2 = id % 2 == 0 ? x + 1 : x;
                int y2 = id % 2 == 0 ? y : y + 1;
                float a = Value(x, y);
                float b = Value(x2, y2);
                double t = Math.Abs(b - a) < 1e-9 ? 0.5 : (iso - a) / (b - a);
                t = Math.Max(0.0, Math.Min(1.0, t));
                // Grid points are padded by one; take it off so points are in sample positions.
                return new P2(x + (x2 - x) * t - 1, y + (y2 - y) * t - 1);
            }

            var nextEdge = new Dictionary<int, int>();
            var edges = new int[4];
            var inside = new bool[4];
            var cornerX = new int[4];
            var cornerY = new int[4];
            var inOut = new List<int>(2);
            var outIn = new List<int>(2);
            for (int cy = 0; cy < gh - 1; cy++)
            {
                for (int cx = 0; cx < gw - 1; cx++)
                {
                    // Corners anticlockwise from the bottom left, and the edge leaving each one anticlockwise.
                    cornerX[0] = cx; cornerY[0] = cy;
                    cornerX[1] = cx + 1; cornerY[1] = cy;
                    cornerX[2] = cx + 1; cornerY[2] = cy + 1;
                    cornerX[3] = cx; cornerY[3] = cy + 1;
                    int mask = 0;
                    float sum = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        float v = Value(cornerX[k], cornerY[k]);
                        sum += v;
                        inside[k] = v > iso;
                        mask |= inside[k] ? 1 << k : 0;
                    }

                    if (mask == 0 || mask == 15)
                    {
                        continue;
                    }

                    edges[0] = HorizontalId(cx, cy);
                    edges[1] = VerticalId(cx + 1, cy);
                    edges[2] = HorizontalId(cx, cy + 1);
                    edges[3] = VerticalId(cx, cy);
                    inOut.Clear();
                    outIn.Clear();
                    for (int k = 0; k < 4; k++)
                    {
                        bool from = inside[k];
                        bool to = inside[(k + 1) % 4];
                        if (from && !to)
                        {
                            inOut.Add(k);
                        }
                        else if (!from && to)
                        {
                            outIn.Add(k);
                        }
                    }

                    // An outline leaves the cell where the inside ends and comes in where it starts, so it runs
                    // from an in-to-out edge to an out-to-in one. A saddle has two of each: join the insides
                    // through the middle if the middle is inside, otherwise keep them apart.
                    if (inOut.Count == 1)
                    {
                        nextEdge[edges[inOut[0]]] = edges[outIn[0]];
                    }
                    else
                    {
                        bool middleInside = sum / 4f > iso;
                        foreach (var k in inOut)
                        {
                            nextEdge[edges[k]] = edges[middleInside ? (k + 1) % 4 : (k + 3) % 4];
                        }
                    }
                }
            }

            var loops = new List<List<P2>>();
            var done = new HashSet<int>();
            foreach (var start in nextEdge.Keys)
            {
                if (done.Contains(start))
                {
                    continue;
                }

                var loop = new List<P2>();
                int edge = start;
                while (done.Add(edge))
                {
                    loop.Add(Crossing(edge));
                    if (!nextEdge.TryGetValue(edge, out edge))
                    {
                        break;
                    }
                }

                if (loop.Count >= 3)
                {
                    loops.Add(loop);
                }
            }

            return loops;
        }

        /// <summary>A closed outline with points dropped while it stays within tolerance of the original (Douglas-Peucker).</summary>
        internal static List<P2> Simplify(List<P2> loop, double tolerance)
        {
            int n = loop.Count;
            if (n < 4)
            {
                return new List<P2>(loop);
            }

            // Split at the point farthest from the first, then simplify both halves as open lines.
            int far = 0;
            double farthest = -1;
            for (int i = 1; i < n; i++)
            {
                double d = (loop[i] - loop[0]).Length;
                if (d > farthest)
                {
                    farthest = d;
                    far = i;
                }
            }

            var keep = new bool[n + 1];
            keep[0] = keep[far] = keep[n] = true;
            P2 At(int i) => loop[i % n];
            var stack = new Stack<(int, int)>();
            stack.Push((0, far));
            stack.Push((far, n));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                double worst = -1;
                int worstAt = -1;
                for (int i = a + 1; i < b; i++)
                {
                    double d = DistanceToSegment(At(i), At(a), At(b));
                    if (d > worst)
                    {
                        worst = d;
                        worstAt = i;
                    }
                }

                if (worstAt >= 0 && worst > tolerance)
                {
                    keep[worstAt] = true;
                    stack.Push((a, worstAt));
                    stack.Push((worstAt, b));
                }
            }

            var result = new List<P2>();
            for (int i = 0; i < n; i++)
            {
                if (keep[i])
                {
                    result.Add(loop[i]);
                }
            }

            return result;
        }

        private static double DistanceToSegment(P2 p, P2 a, P2 b)
        {
            var ab = b - a;
            double lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
            if (lengthSquared < 1e-12)
            {
                return (p - a).Length;
            }

            double t = Math.Max(0.0, Math.Min(1.0, ((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / lengthSquared));
            return (p - (a + ab * t)).Length;
        }

        /// <summary>Positive for anticlockwise.</summary>
        internal static double SignedArea(List<P2> loop)
        {
            double sum = 0;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                sum += (loop[j].X * loop[i].Y) - (loop[i].X * loop[j].Y);
            }

            return sum / 2.0;
        }

        internal static bool Contains(List<P2> loop, P2 p)
        {
            bool inside = false;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                var a = loop[i];
                var b = loop[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        // ---- Triangulating ----

        /// <summary>
        /// Triangles filling an anticlockwise outline less its clockwise holes, as index triples into points
        /// (outer first, then each hole), each anticlockwise. Holes are joined to the outline by a cut, then
        /// ears are clipped off the result.
        /// </summary>
        internal static List<int> Triangulate(List<P2> points, List<int> outer, List<List<int>> holes)
        {
            var polygon = new List<int>(outer);
            var ordered = new List<List<int>>(holes);
            ordered.Sort((a, b) => MaxX(points, b).CompareTo(MaxX(points, a)));
            foreach (var hole in ordered)
            {
                if (hole.Count < 3)
                {
                    continue;
                }

                int right = 0;
                for (int i = 1; i < hole.Count; i++)
                {
                    if (points[hole[i]].X > points[hole[right]].X)
                    {
                        right = i;
                    }
                }

                int at = FindBridge(points, polygon, points[hole[right]]);
                if (at < 0)
                {
                    continue; // nowhere to join it: leave the hole filled
                }

                var joined = new List<int>(polygon.Count + hole.Count + 2);
                for (int i = 0; i <= at; i++)
                {
                    joined.Add(polygon[i]);
                }

                for (int k = 0; k <= hole.Count; k++)
                {
                    joined.Add(hole[(right + k) % hole.Count]);
                }

                joined.Add(polygon[at]);
                for (int i = at + 1; i < polygon.Count; i++)
                {
                    joined.Add(polygon[i]);
                }

                polygon = joined;
            }

            return ClipEars(points, polygon);
        }

        private static double MaxX(List<P2> points, List<int> loop)
        {
            double max = double.MinValue;
            foreach (var i in loop)
            {
                max = Math.Max(max, points[i].X);
            }

            return max;
        }

        private static double Cross(P2 a, P2 b) => a.X * b.Y - a.Y * b.X;

        private static double Orient(P2 a, P2 b, P2 c) => Cross(b - a, c - a);

        private static bool Same(P2 a, P2 b) => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

        // Where in the polygon a hole's rightmost point m can be joined to: the vertex it can see first along a
        // ray to the right (after Eberly, "Triangulation by Ear Clipping"). -1 if the ray hits nothing.
        private static int FindBridge(List<P2> points, List<int> polygon, P2 m)
        {
            int n = polygon.Count;
            double nearest = double.MaxValue;
            int edge = -1;
            for (int i = 0; i < n; i++)
            {
                var a = points[polygon[i]];
                var b = points[polygon[(i + 1) % n]];
                if (a.Y == b.Y || m.Y < Math.Min(a.Y, b.Y) || m.Y > Math.Max(a.Y, b.Y))
                {
                    continue; // flat, or doesn't cross the ray's line
                }

                double x = a.X + (m.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                if (x >= m.X && x < nearest)
                {
                    nearest = x;
                    edge = i;
                }
            }

            if (edge < 0)
            {
                return -1;
            }

            var hit = new P2(nearest, m.Y);
            int ia = edge;
            int ib = (edge + 1) % n;
            int candidate = points[polygon[ia]].X >= points[polygon[ib]].X ? ia : ib;
            if (Same(points[polygon[ia]], hit))
            {
                candidate = ia;
            }
            else if (Same(points[polygon[ib]], hit))
            {
                candidate = ib;
            }
            else
            {
                // A reflex vertex inside the triangle m, hit, candidate would block the view; the one at the
                // smallest angle from the ray is visible.
                var p = points[polygon[candidate]];
                double bestTan = double.MaxValue;
                double bestDistance = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    var v = points[polygon[i]];
                    if (Same(v, p) || v.X <= m.X || !InTriangle(m, hit, p, v))
                    {
                        continue;
                    }

                    var prev = points[polygon[(i + n - 1) % n]];
                    var next = points[polygon[(i + 1) % n]];
                    if (Orient(prev, v, next) > 0)
                    {
                        continue; // convex
                    }

                    double tan = Math.Abs(v.Y - m.Y) / (v.X - m.X);
                    double distance = (v - m).Length;
                    if (tan < bestTan - 1e-12 || (Math.Abs(tan - bestTan) <= 1e-12 && distance < bestDistance))
                    {
                        bestTan = tan;
                        bestDistance = distance;
                        candidate = i;
                    }
                }
            }

            // The vertex may be in the polygon more than once (it already joins another hole): use the copy
            // whose corner faces m.
            var target = points[polygon[candidate]];
            for (int i = 0; i < n; i++)
            {
                if (Same(points[polygon[i]], target) && CornerFaces(points[polygon[(i + n - 1) % n]], target, points[polygon[(i + 1) % n]], m))
                {
                    return i;
                }
            }

            return candidate;
        }

        // Whether the direction from cur to m points into the polygon's inside at cur (inside on the left).
        private static bool CornerFaces(P2 prev, P2 cur, P2 next, P2 m)
        {
            var toNext = next - cur;
            var toPrev = prev - cur;
            var toM = m - cur;
            if (Cross(toNext, toPrev) >= 0)
            {
                return Cross(toNext, toM) > 0 && Cross(toM, toPrev) > 0;
            }

            return Cross(toNext, toM) > 0 || Cross(toM, toPrev) > 0;
        }

        private static bool InTriangle(P2 a, P2 b, P2 c, P2 p)
        {
            const double eps = 1e-9;
            double area = Orient(a, b, c);
            if (Math.Abs(area) < eps)
            {
                return false;
            }

            double s = area > 0 ? 1 : -1;
            return s * Orient(a, b, p) >= -eps && s * Orient(b, c, p) >= -eps && s * Orient(c, a, p) >= -eps;
        }

        private static List<int> ClipEars(List<P2> points, List<int> polygon)
        {
            var triangles = new List<int>();
            int n = polygon.Count;
            if (n < 3)
            {
                return triangles;
            }

            var prev = new int[n];
            var next = new int[n];
            for (int i = 0; i < n; i++)
            {
                prev[i] = (i + n - 1) % n;
                next[i] = (i + 1) % n;
            }

            P2 At(int i) => points[polygon[i]];

            void Remove(int i)
            {
                next[prev[i]] = next[i];
                prev[next[i]] = prev[i];
            }

            void Emit(int a, int b, int c)
            {
                if (Orient(At(a), At(b), At(c)) > 1e-12)
                {
                    triangles.Add(polygon[a]);
                    triangles.Add(polygon[b]);
                    triangles.Add(polygon[c]);
                }
            }

            bool IsEar(int b)
            {
                int a = prev[b];
                int c = next[b];
                var pa = At(a);
                var pb = At(b);
                var pc = At(c);
                if (Orient(pa, pb, pc) <= 1e-12)
                {
                    return false;
                }

                for (int v = next[c]; v != a; v = next[v])
                {
                    var pv = At(v);
                    if (Same(pv, pa) || Same(pv, pb) || Same(pv, pc))
                    {
                        continue;
                    }

                    if (Orient(At(prev[v]), pv, At(next[v])) <= 0 && InTriangle(pa, pb, pc, pv))
                    {
                        return false;
                    }
                }

                return true;
            }

            int count = n;
            int current = 0;
            int misses = 0;
            while (count > 3)
            {
                if (IsEar(current))
                {
                    Emit(prev[current], current, next[current]);
                    int after = next[current];
                    Remove(current);
                    count--;
                    current = after;
                    misses = 0;
                    continue;
                }

                current = next[current];
                if (++misses < count)
                {
                    continue;
                }

                // A whole lap without an ear: the outline is degenerate somewhere (a straight-through point, or
                // an edge crossing another after simplifying). Drop a straight-through point if there is one,
                // otherwise clip the most convex corner anyway, so it always finishes.
                int pick = -1;
                double best = double.MinValue;
                int v = current;
                for (int k = 0; k < count; k++, v = next[v])
                {
                    double o = Orient(At(prev[v]), At(v), At(next[v]));
                    if (Math.Abs(o) <= 1e-12)
                    {
                        pick = v;
                        break;
                    }

                    if (o > best)
                    {
                        best = o;
                        pick = v;
                    }
                }

                Emit(prev[pick], pick, next[pick]);
                current = next[pick];
                Remove(pick);
                count--;
                misses = 0;
            }

            Emit(prev[current], current, next[current]);
            return triangles;
        }

        // ---- Building the mesh ----

        private struct Frame
        {
            public double MetresPerPixel;
            public double CentreX;
            public double CentreY;
            public int Width;
            public int Height;
            public double HalfThickness;
            public double UvInset;
            public double SmoothingCos;
        }

        private sealed class MeshBuilder
        {
            private readonly List<float> _positions = new List<float>();
            private readonly List<float> _normals = new List<float>();
            private readonly List<float> _uvs = new List<float>();
            private readonly List<int> _triangles = new List<int>();

            public int TriangleCount => _triangles.Count / 3;

            public int Add(P2 pixel, double z, double nx, double ny, double nz, P2 uvPixel, in Frame frame)
            {
                _positions.Add((float)((pixel.X - frame.CentreX) * frame.MetresPerPixel));
                _positions.Add((float)((pixel.Y - frame.CentreY) * frame.MetresPerPixel));
                _positions.Add((float)z);
                _normals.Add((float)nx);
                _normals.Add((float)ny);
                _normals.Add((float)nz);
                _uvs.Add((float)(uvPixel.X / frame.Width));
                _uvs.Add((float)(uvPixel.Y / frame.Height));
                return _positions.Count / 3 - 1;
            }

            public void Triangle(int a, int b, int c)
            {
                _triangles.Add(a);
                _triangles.Add(b);
                _triangles.Add(c);
            }

            public void CopyTo(PictureModel model)
            {
                model.Positions = _positions.ToArray();
                model.Normals = _normals.ToArray();
                model.Uvs = _uvs.ToArray();
                model.Triangles = _triangles.ToArray();
            }
        }

        // One slab: the outlines filled on the front (-Z) and back (+Z), and walls around every outline.
        private static void Extrude(MeshBuilder mesh, List<List<P2>> loops, in Frame frame)
        {
            double front = -frame.HalfThickness;
            double back = frame.HalfThickness;

            // Holes belong to the smallest outer outline around them.
            var outers = new List<int>();
            var holesOf = new Dictionary<int, List<int>>();
            var areas = new double[loops.Count];
            for (int i = 0; i < loops.Count; i++)
            {
                areas[i] = SignedArea(loops[i]);
                if (areas[i] > 0)
                {
                    outers.Add(i);
                    holesOf[i] = new List<int>();
                }
            }

            for (int i = 0; i < loops.Count; i++)
            {
                if (areas[i] >= 0)
                {
                    continue;
                }

                int owner = -1;
                foreach (var o in outers)
                {
                    if (Contains(loops[o], loops[i][0]) && (owner < 0 || areas[o] < areas[owner]))
                    {
                        owner = o;
                    }
                }

                if (owner >= 0)
                {
                    holesOf[owner].Add(i);
                }
            }

            foreach (var o in outers)
            {
                var points = new List<P2>(loops[o]);
                var outer = Range(0, loops[o].Count);
                var holes = new List<List<int>>();
                foreach (var h in holesOf[o])
                {
                    holes.Add(Range(points.Count, loops[h].Count));
                    points.AddRange(loops[h]);
                }

                var triangles = Triangulate(points, outer, holes);
                var frontIds = new int[points.Count];
                var backIds = new int[points.Count];
                for (int i = 0; i < points.Count; i++)
                {
                    frontIds[i] = mesh.Add(points[i], front, 0, 0, -1, points[i], frame);
                    backIds[i] = mesh.Add(points[i], back, 0, 0, 1, points[i], frame);
                }

                for (int t = 0; t < triangles.Count; t += 3)
                {
                    // Anticlockwise in the picture is clockwise seen from behind it (+Z), which is Unity's front
                    // face there; the front cap (seen from -Z) needs the other order.
                    mesh.Triangle(backIds[triangles[t]], backIds[triangles[t + 1]], backIds[triangles[t + 2]]);
                    mesh.Triangle(frontIds[triangles[t]], frontIds[triangles[t + 2]], frontIds[triangles[t + 1]]);
                }

                Walls(mesh, loops[o], frame, front, back);
                foreach (var h in holesOf[o])
                {
                    Walls(mesh, loops[h], frame, front, back);
                }
            }
        }

        private static List<int> Range(int start, int count)
        {
            var list = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(start + i);
            }

            return list;
        }

        // Walls around one outline. The item is on the outline's left, so each wall faces right of the way the
        // outline runs. Corners gentler than the smoothing angle share a normal; sharper ones keep their own.
        private static void Walls(MeshBuilder mesh, List<P2> loop, in Frame frame, double front, double back)
        {
            int n = loop.Count;
            var faces = new P2[n];
            for (int i = 0; i < n; i++)
            {
                var d = loop[(i + 1) % n] - loop[i];
                double length = d.Length;
                faces[i] = length > 1e-12 ? new P2(d.Y / length, -d.X / length) : new P2(0, 0);
            }

            // Each point's normal for the wall before and after it, and where its colour comes from: a little
            // way inside the item, past the edge's mix of item and background.
            var before = new P2[n];
            var after = new P2[n];
            var uv = new P2[n];
            for (int i = 0; i < n; i++)
            {
                var a = faces[(i + n - 1) % n];
                var b = faces[i];
                var mean = a + b;
                double length = mean.Length;
                var shared = length > 1e-9 ? mean * (1.0 / length) : b;
                bool smooth = a.X * b.X + a.Y * b.Y >= frame.SmoothingCos;
                before[i] = smooth ? shared : a;
                after[i] = smooth ? shared : b;
                var inward = shared * -frame.UvInset;
                uv[i] = new P2(
                    Math.Max(0.5, Math.Min(frame.Width - 0.5, loop[i].X + inward.X)),
                    Math.Max(0.5, Math.Min(frame.Height - 0.5, loop[i].Y + inward.Y)));
            }

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                var ni = after[i];
                var nj = before[j];
                int a = mesh.Add(loop[i], front, ni.X, ni.Y, 0, uv[i], frame);
                int b = mesh.Add(loop[j], front, nj.X, nj.Y, 0, uv[j], frame);
                int c = mesh.Add(loop[j], back, nj.X, nj.Y, 0, uv[j], frame);
                int d = mesh.Add(loop[i], back, ni.X, ni.Y, 0, uv[i], frame);
                mesh.Triangle(a, b, c);
                mesh.Triangle(a, c, d);
            }
        }
    }
}
