using RaidZero.Items.Editor;
using P2 = RaidZero.Items.Editor.PictureModelBuilder.P2;

namespace Game.Shared.Tests;

/// <summary>Model From Picture's geometry (the Unity item tools' PictureModelBuilder, linked in).</summary>
public class PictureModelBuilderTests
{
    private static readonly byte[] Clear = { 0, 0, 0, 0 };
    private static readonly byte[] Solid = { 40, 60, 80, 255 };
    private static readonly byte[] White = { 255, 255, 255, 255 };
    private static readonly byte[] Black = { 0, 0, 0, 255 };
    private static readonly byte[] Dark = { 30, 30, 30, 255 };

    private static readonly PictureModelSettings Metre = new PictureModelSettings { Length = 1f, Thickness = 0.1f };

    // A picture from a function of each pixel (x right, y up).
    private static PicturePixels Draw(int width, int height, Func<int, int, byte[]> colour)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Array.Copy(colour(x, y), 0, rgba, (y * width + x) * 4, 4);
            }
        }

        return new PicturePixels(width, height, rgba);
    }

    private static bool In(int x, int y, int x0, int y0, int x1, int y1) => x >= x0 && x < x1 && y >= y0 && y < y1;

    // Enclosed volume. Positive only if every face points out (Unity's front faces are clockwise from outside).
    private static double Volume(PictureModel m)
    {
        double v = 0;
        var p = m.Positions;
        for (int t = 0; t < m.Triangles.Length; t += 3)
        {
            int a = m.Triangles[t] * 3, b = m.Triangles[t + 1] * 3, c = m.Triangles[t + 2] * 3;
            v += p[a] * ((double)p[b + 1] * p[c + 2] - (double)p[b + 2] * p[c + 1])
                - p[a + 1] * ((double)p[b] * p[c + 2] - (double)p[b + 2] * p[c])
                + p[a + 2] * ((double)p[b] * p[c + 1] - (double)p[b + 1] * p[c]);
        }

        return v / 6.0;
    }

    // Triangles whose winding (Unity's: cross(b - a, c - a) points to the viewer) disagrees with their normals.
    private static int FacesPointingIn(PictureModel m)
    {
        int bad = 0;
        var p = m.Positions;
        var n = m.Normals;
        for (int t = 0; t < m.Triangles.Length; t += 3)
        {
            int a = m.Triangles[t] * 3, b = m.Triangles[t + 1] * 3, c = m.Triangles[t + 2] * 3;
            double ux = p[b] - p[a], uy = p[b + 1] - p[a + 1], uz = p[b + 2] - p[a + 2];
            double vx = p[c] - p[a], vy = p[c + 1] - p[a + 1], vz = p[c + 2] - p[a + 2];
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            double dot = cx * (n[a] + n[b] + n[c]) + cy * (n[a + 1] + n[b + 1] + n[c + 1]) + cz * (n[a + 2] + n[b + 2] + n[c + 2]);
            if (dot <= 0)
            {
                bad++;
            }
        }

        return bad;
    }

    private static (float Min, float Max) Range(PictureModel m, int axis)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int i = axis; i < m.Positions.Length; i += 3)
        {
            min = Math.Min(min, m.Positions[i]);
            max = Math.Max(max, m.Positions[i]);
        }

        return (min, max);
    }

    [Fact]
    public void Rectangle_IsScaledToItsLength_CentredAndClosed()
    {
        var picture = Draw(200, 100, (x, y) => In(x, y, 20, 30, 180, 70) ? Solid : Clear);

        var model = PictureModelBuilder.Build(picture, null, Metre);

        Assert.True(model.UsedTransparency);
        Assert.Single(model.Outlines);
        Assert.Equal(1.0, model.WidthMetres, 4);
        Assert.Equal(0.25, model.HeightMetres, 4);
        Assert.Equal(1.0 * 0.25 * 0.1, Volume(model), 3);
        Assert.Equal(0, FacesPointingIn(model));
        Assert.Equal((-0.05f, 0.05f), Range(model, 2));
        var (minX, maxX) = Range(model, 0);
        Assert.True(Math.Abs(minX + maxX) < 0.02, "centred across");
        Assert.True(model.TriangleCount < 60, model.TriangleCount + " triangles for a box");
    }

    [Fact]
    public void Texture_IsOpaque_WithTheBackgroundTakingTheItemsColour()
    {
        var picture = Draw(200, 100, (x, y) => In(x, y, 20, 30, 180, 70) ? Solid : Clear);

        var model = PictureModelBuilder.Build(picture, null, Metre);

        Assert.All(Enumerable.Range(0, model.Texture.Length / 4), i => Assert.Equal(255, model.Texture[i * 4 + 3]));
        Assert.Equal(Solid.Take(3), model.Texture.Take(3));
        Assert.Equal(200, model.TextureWidth);
        Assert.Equal(100, model.TextureHeight);
    }

    [Fact]
    public void Hole_IsCutThrough()
    {
        var picture = Draw(200, 100, (x, y) => In(x, y, 20, 20, 180, 80) && !In(x, y, 60, 40, 140, 60) ? Solid : Clear);

        var model = PictureModelBuilder.Build(picture, null, Metre);

        Assert.Equal(2, model.Outlines.Count);
        double area = (160 * 60 - 80 * 20) / (160.0 * 160.0);
        Assert.Equal(area * 0.1, Volume(model), 3);
        Assert.Equal(0, FacesPointingIn(model));
    }

    [Fact]
    public void PlainBackground_IsCutByColour_WithEnclosedHolesCutUnlessAsked()
    {
        var picture = Draw(200, 100, (x, y) =>
            In(x, y, 20, 20, 180, 80) && !In(x, y, 60, 40, 140, 60) && !In(x, y, 30, 30, 32, 32) ? Dark : White);

        var cut = PictureModelBuilder.Build(picture, null, Metre);
        var filled = PictureModelBuilder.Build(picture, null, new PictureModelSettings { Length = 1f, Thickness = 0.1f, CutEnclosedBackground = false });

        Assert.False(cut.UsedTransparency);
        Assert.Equal(2, cut.Outlines.Count); // the big hole is cut; the 2x2 speck is too small to be one
        Assert.Single(filled.Outlines);
    }

    [Fact]
    public void Dust_IsDropped_ButSeparatePartsAreKept()
    {
        var dusty = Draw(200, 100, (x, y) => In(x, y, 20, 20, 180, 80) || In(x, y, 190, 5, 192, 7) ? Dark : White);
        var parts = Draw(200, 100, (x, y) => In(x, y, 10, 40, 120, 70) || In(x, y, 140, 10, 190, 50) ? Dark : White);

        Assert.Single(PictureModelBuilder.Build(dusty, null, Metre).Outlines);
        Assert.Equal(2, PictureModelBuilder.Build(parts, null, Metre).Outlines.Count);
    }

    [Fact]
    public void EmptyPicture_SaysSo()
    {
        var empty = Draw(50, 50, (x, y) => Clear);

        var e = Assert.Throws<PictureModelException>(() => PictureModelBuilder.Build(empty, null, Metre));
        Assert.Contains("no item", e.Message);
    }

    [Fact]
    public void ThicknessMap_MakesTheWhitePartsThick_AndTheBlackPartsThin()
    {
        var picture = Draw(200, 100, (x, y) => In(x, y, 20, 30, 180, 70) ? Solid : Clear);
        var map = Draw(20, 10, (x, y) => x < 10 ? White : Black); // left half of the picture white
        var settings = new PictureModelSettings { Length = 1f, Thickness = 0.1f, Thinnest = 0.4f, Steps = 3 };

        var model = PictureModelBuilder.Build(picture, map, settings);

        // Nothing is mid-grey, so the middle step merges into the top one: two layers, not three.
        Assert.Equal(2, model.Layers);
        Assert.Equal(0.05f, Range(model, 2).Max, 4);
        Assert.Equal(0, FacesPointingIn(model));
        double thin = 1.0 * 0.25 * 0.04; // the whole item, 0.4 of the thickness
        double thick = 0.5 * 0.25 * 0.1; // the left half (80 of its 160 pixels), full thickness
        Assert.Equal(thin + thick, Volume(model), 3);
    }

    [Fact]
    public void Circle_ComesOutRound()
    {
        var picture = Draw(300, 300, (x, y) =>
        {
            double dx = x + 0.5 - 150, dy = y + 0.5 - 150;
            return dx * dx + dy * dy < 120 * 120 ? Solid : Clear;
        });

        var model = PictureModelBuilder.Build(picture, null, Metre);

        double expected = Math.PI * 0.25 * 0.1; // a 0.5 m radius disc, 0.1 m thick
        Assert.InRange(Volume(model), expected * 0.99, expected); // a polygon inside the circle, a little smaller
        Assert.InRange(model.Outlines[0].Length / 2, 16, 200);
    }

    [Fact]
    public void BigPicture_IsTracedAtTheWorkingSize()
    {
        var picture = Draw(3000, 1000, (x, y) => In(x, y, 300, 300, 2700, 700) ? Solid : Clear);

        var model = PictureModelBuilder.Build(picture, null, Metre);

        Assert.Equal(1.0 * (400.0 / 2400) * 0.1, Volume(model), 3);
    }

    [Fact]
    public void MarchingSquares_KeepsDiagonalPixelsApart_AndRunsHolesClockwise()
    {
        var diagonal = PictureModelBuilder.MarchingSquares(new float[] { 1, 0, 0, 1 }, 2, 2, 0.5f);
        var ring = PictureModelBuilder.MarchingSquares(new float[] { 1, 1, 1, 1, 0, 1, 1, 1, 1 }, 3, 3, 0.5f);

        Assert.Equal(2, diagonal.Count);
        Assert.All(diagonal, loop => Assert.True(PictureModelBuilder.SignedArea(loop) > 0));
        Assert.Equal(2, ring.Count);
        Assert.Single(ring, loop => PictureModelBuilder.SignedArea(loop) < 0);
    }

    [Fact]
    public void Triangulate_TwoHolesLevelWithEachOther()
    {
        var points = new List<P2> { new P2(0, 0), new P2(100, 0), new P2(100, 100), new P2(0, 100) };
        var holes = new List<List<int>>();
        foreach (var x in new[] { 20.0, 60.0 })
        {
            holes.Add(new List<int> { points.Count, points.Count + 1, points.Count + 2, points.Count + 3 });
            points.AddRange(new[] { new P2(x, 40), new P2(x, 60), new P2(x + 20, 60), new P2(x + 20, 40) });
        }

        var triangles = PictureModelBuilder.Triangulate(points, new List<int> { 0, 1, 2, 3 }, holes);

        Assert.Equal(10000 - 800, TriangleArea(points, triangles, out var clockwise), 6);
        Assert.Equal(0, clockwise);
    }

    // Random star-shaped outlines with random round holes: the triangles must cover exactly the outline less
    // the holes, all anticlockwise.
    [Fact]
    public void Triangulate_RandomOutlinesWithHoles()
    {
        var random = new Random(1);
        int tried = 0;
        for (int trial = 0; trial < 2000; trial++)
        {
            int n = random.Next(3, 40);
            var angles = Enumerable.Range(0, n).Select(_ => random.NextDouble() * Math.PI * 2).OrderBy(a => a).ToList();
            var gaps = angles.Zip(angles.Skip(1), (a, b) => b - a).Append(angles[0] + Math.PI * 2 - angles[n - 1]).ToList();
            if (gaps.Any(g => g < 1e-3 || g >= Math.PI * 0.95))
            {
                continue; // not a simple anticlockwise outline around the origin
            }

            var points = angles.Select(a => { double r = 50 + random.NextDouble() * 50; return new P2(r * Math.Cos(a), r * Math.Sin(a)); }).ToList();
            var outline = points.ToList();
            double expected = PictureModelBuilder.SignedArea(outline);
            var holes = new List<List<int>>();
            var placed = new List<(double X, double Y, double R)>();
            for (int k = random.Next(0, 4); k > 0; k--)
            {
                double r = 3 + random.NextDouble() * 6;
                double angle = random.NextDouble() * Math.PI * 2, distance = random.NextDouble() * (45 - r);
                double hx = distance * Math.Cos(angle), hy = distance * Math.Sin(angle);
                if (placed.Count > 0 && random.Next(4) == 0)
                {
                    hx = placed[0].X + 20; // level with another hole
                    hy = placed[0].Y;
                }

                if (!PictureModelBuilder.Contains(outline, new P2(hx, hy)) || DistanceToOutline(outline, hx, hy) < r + 1
                    || placed.Any(h => Math.Sqrt((h.X - hx) * (h.X - hx) + (h.Y - hy) * (h.Y - hy)) < h.R + r + 1))
                {
                    continue;
                }

                placed.Add((hx, hy, r));
                int sides = random.Next(3, 12);
                var hole = new List<int>();
                for (int j = 0; j < sides; j++)
                {
                    double a = -j * Math.PI * 2 / sides; // clockwise
                    hole.Add(points.Count);
                    points.Add(new P2(hx + r * Math.Cos(a), hy + r * Math.Sin(a)));
                }

                expected += PictureModelBuilder.SignedArea(hole.Select(i => points[i]).ToList());
                holes.Add(hole);
            }

            var triangles = PictureModelBuilder.Triangulate(points, Enumerable.Range(0, n).ToList(), holes);

            Assert.Equal(expected, TriangleArea(points, triangles, out var clockwise), 6);
            Assert.Equal(0, clockwise);
            tried++;
        }

        Assert.True(tried > 500, "only " + tried + " outlines tried");
    }

    private static double TriangleArea(List<P2> points, List<int> triangles, out int clockwise)
    {
        double total = 0;
        clockwise = 0;
        for (int t = 0; t < triangles.Count; t += 3)
        {
            var a = points[triangles[t]];
            var b = points[triangles[t + 1]];
            var c = points[triangles[t + 2]];
            double area = ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2;
            total += area;
            if (area <= 0)
            {
                clockwise++;
            }
        }

        return total;
    }

    private static double DistanceToOutline(List<P2> outline, double x, double y)
    {
        double best = double.MaxValue;
        for (int i = 0; i < outline.Count; i++)
        {
            var a = outline[i];
            var b = outline[(i + 1) % outline.Count];
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double t = Math.Max(0, Math.Min(1, ((x - a.X) * ex + (y - a.Y) * ey) / (ex * ex + ey * ey)));
            double dx = a.X + ex * t - x, dy = a.Y + ey * t - y;
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
        }

        return best;
    }
}
