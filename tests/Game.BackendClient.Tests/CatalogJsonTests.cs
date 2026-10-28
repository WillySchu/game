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
        // Unity instead (Assets/Data/Items), or run Raid Zero > Items > Import Catalog first to bring a hand
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
}
