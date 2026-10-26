using System.Text.Json.Nodes;
using RaidZero.Backend.Catalog;
using RaidZero.Shared.Catalog;

namespace Game.Backend.Tests;

public class CatalogLoaderTests
{
    /// <summary>
    /// The CI check from the backend doc: the committed catalog must load, and its declared version must
    /// match its contents. If this fails, someone edited items without re-exporting from Unity.
    /// </summary>
    [Fact]
    public void CommittedCatalog_LoadsAndItsVersionIsCurrent()
    {
        var catalog = CatalogLoader.LoadFile(TestPaths.CatalogPath);

        Assert.True(catalog.Contains(WellKnownDefs.Stash));
        Assert.True(catalog.Contains(WellKnownDefs.Character));
        Assert.True(catalog.Count > 2);
    }

    [Fact]
    public void ParsesSnakeCaseFields()
    {
        var catalog = CatalogLoader.LoadFile(TestPaths.CatalogPath);

        var mag = catalog.Get("mag_ak_30");
        Assert.Equal(30, mag.Slots.Single().MaxQuantity);
        Assert.True(catalog.Get("rifle_ak_basic").HasDurability);
        Assert.Equal(60, catalog.Get("ammo_545_ps").MaxStack);
    }

    [Fact]
    public void StaleVersion_IsRejectedWithTheComputedValue()
    {
        var json = Edit(doc => doc["catalog_version"] = "0000000000000000");

        var ex = Assert.Throws<CatalogLoadException>(() => CatalogLoader.Parse(json));

        var expected = CatalogLoader.LoadFile(TestPaths.CatalogPath).Version;
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void RuleChangeWithoutReexport_IsRejected()
    {
        var json = Edit(doc => ItemNamed(doc, "ammo_545_ps")["max_stack"] = 61);

        Assert.Throws<CatalogLoadException>(() => CatalogLoader.Parse(json));
    }

    [Fact]
    public void CosmeticChange_DoesNotChangeTheVersion()
    {
        var json = Edit(doc => ItemNamed(doc, "ammo_545_ps")["display_name"] = "Renamed ammo");

        var catalog = CatalogLoader.Parse(json);

        Assert.Equal("Renamed ammo", catalog.Get("ammo_545_ps").DisplayName);
    }

    [Fact]
    public void MissingRootContainer_IsRejected()
    {
        var json = Edit(doc => doc["items"]!.AsArray().Remove(ItemNamed(doc, "stash")));

        var ex = Assert.Throws<CatalogLoadException>(() => CatalogLoader.Parse(json));
        Assert.Contains("'stash'", ex.Message);
    }

    [Fact]
    public void InvalidDefinitions_AreReported()
    {
        var json = Edit(doc => ItemNamed(doc, "loot_fuse")["width"] = 0);

        var ex = Assert.Throws<CatalogLoadException>(() => CatalogLoader.Parse(json));
        Assert.Contains("loot_fuse: width", ex.Message);
    }

    [Fact]
    public void BrokenJson_IsReported()
    {
        var ex = Assert.Throws<CatalogLoadException>(() => CatalogLoader.Parse("{ \"items\": [", "broken.json"));
        Assert.Contains("broken.json is not valid JSON", ex.Message);
    }

    [Fact]
    public void MissingFile_IsReported()
    {
        Assert.Throws<CatalogLoadException>(() => CatalogLoader.LoadFile(Path.Combine(TestPaths.RepoRoot, "nope.json")));
    }

    private static string Edit(Action<JsonObject> change)
    {
        var doc = JsonNode.Parse(File.ReadAllText(TestPaths.CatalogPath), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!.AsObject();
        change(doc);
        return doc.ToJsonString();
    }

    private static JsonObject ItemNamed(JsonObject doc, string defId) =>
        doc["items"]!.AsArray().Single(i => (string?)i!["def_id"] == defId)!.AsObject();
}
