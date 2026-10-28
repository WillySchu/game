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

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"items\": []}")]
    public void Nonsense_IsRefused(string json)
    {
        Assert.Throws<InvalidDataException>(() => CatalogJson.Parse(json));
    }
}
