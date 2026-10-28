using Game.Backend.Tests;
using RaidZero.Backend.Catalog;

namespace RaidZero.BackendApi.Tests;

public class CatalogJsonTests
{
    [Fact]
    public void TheRealCatalog_ReadsTheSameAsTheBackendReadsIt()
    {
        var unity = CatalogJson.LoadFile(TestPaths.CatalogPath);
        var backend = CatalogLoader.LoadFile(TestPaths.CatalogPath);

        Assert.Equal(backend.Version, unity.Version);
        Assert.Equal(backend.Count, unity.Count);
        var rounds = unity.Get("mag_ak_30").Slots.Single();
        Assert.Equal(("rounds", 30), (rounds.Name, rounds.MaxQuantity));
    }

    [Fact]
    public void AStaleVersion_IsRefused()
    {
        var json = File.ReadAllText(TestPaths.CatalogPath);
        var declared = CatalogLoader.LoadFile(TestPaths.CatalogPath).Version;

        var error = Assert.Throws<InvalidDataException>(() => CatalogJson.Parse(json.Replace(declared, "0000000000000000")));

        Assert.Contains(declared, error.Message);
    }

    [Fact]
    public void Writing_ThenReading_GivesTheSameCatalog_ToUnityAndToTheBackend()
    {
        var original = CatalogJson.LoadFile(TestPaths.CatalogPath);

        var written = CatalogJson.Write(original.All);

        Assert.Equal(original.Version, CatalogJson.Parse(written).Version);
        Assert.Equal(original.Version, CatalogLoader.Parse(written).Version);
        var reread = CatalogJson.Parse(written);
        foreach (var definition in original.All)
        {
            var copy = reread.Get(definition.DefId);
            Assert.Equal(definition.DisplayName, copy.DisplayName);
            Assert.Equal(definition.Tags, copy.Tags);
            Assert.Equal(definition.Slots.Select(s => (s.Name, string.Join(",", s.Accepts), s.MaxQuantity)), copy.Slots.Select(s => (s.Name, string.Join(",", s.Accepts), s.MaxQuantity)));
        }
    }

    [Fact]
    public void Writing_IsTheSameWhateverTheOrder_AndLeavesOutDefaults()
    {
        var definitions = CatalogJson.LoadFile(TestPaths.CatalogPath).All.ToList();

        var forwards = CatalogJson.Write(definitions);
        definitions.Reverse();
        var backwards = CatalogJson.Write(definitions);

        Assert.Equal(forwards, backwards);
        var fuse = Newtonsoft.Json.Linq.JObject.Parse(forwards)["items"]!.Single(i => (string?)i["def_id"] == "loot_fuse");
        Assert.Equal(new[] { "def_id", "display_name", "tags" }, ((Newtonsoft.Json.Linq.JObject)fuse).Properties().Select(p => p.Name));
    }

    [Fact]
    public void TheCommittedCatalog_IsInTheExportersFormat()
    {
        // Unity's exporter writes this file. If this fails, the file was edited by hand: change the item in
        // Unity instead (Assets/_Project/Data/Items), or run Raid Zero > Items > Import Catalog first to bring a hand
        // edit into the assets.
        var text = File.ReadAllText(TestPaths.CatalogPath).Replace("\r\n", "\n");

        Assert.Equal(CatalogJson.Write(CatalogJson.Parse(text).All), text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"items\": []}")]
    public void Nonsense_IsRefused(string json)
    {
        Assert.Throws<InvalidDataException>(() => CatalogJson.Parse(json));
    }

    [Fact]
    public void Pockets_SurviveAWriteAndRead_InOrder_ForUnityAndTheBackend()
    {
        var definitions = CatalogJson.LoadFile(TestPaths.CatalogPath).All.ToList();
        var rig = definitions.Single(d => d.DefId == "rig_chest_basic");
        rig.Grids = [new() { Name = "right", Width = 1, Height = 2 }, new() { Name = "left", Width = 2, Height = 2 }];

        var written = CatalogJson.Write(definitions);
        var unity = CatalogJson.Parse(written).Get("rig_chest_basic");
        var backend = CatalogLoader.Parse(written).Get("rig_chest_basic");

        foreach (var reread in new[] { unity, backend })
        {
            Assert.Equal([("right", 1, 2), ("left", 2, 2)], reread.Grids.Select(g => (g.Name, g.Width, g.Height)));
        }
    }

    [Fact]
    public void TheFormatBeforeNamedGrids_IsRefused_RatherThanReadWithoutGrids()
    {
        var text = File.ReadAllText(TestPaths.CatalogPath);
        var old = Newtonsoft.Json.Linq.JObject.Parse(text);
        var backpack = old["items"]!.Single(i => (string?)i["def_id"] == "backpack_small")!;
        backpack["grid"] = new Newtonsoft.Json.Linq.JObject { ["width"] = 4, ["height"] = 5 };
        ((Newtonsoft.Json.Linq.JObject)backpack).Remove("grids");
        var json = old.ToString();

        Assert.Contains("old format", Assert.Throws<InvalidDataException>(() => CatalogJson.Parse(json)).Message);
        Assert.Contains("old format", Assert.Throws<CatalogLoadException>(() => CatalogLoader.Parse(json)).Message);
    }
}
