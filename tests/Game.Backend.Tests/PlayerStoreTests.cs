using System.Text.Json;
using Dapper;
using RaidZero.Backend.Catalog;
using RaidZero.Backend.Data;
using RaidZero.Backend.Players;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace Game.Backend.Tests;

public sealed class PlayerStoreTests : IDisposable
{
    private static readonly ItemCatalog Catalog = CatalogLoader.LoadFile(TestPaths.CatalogPath);

    private readonly TempDatabase _temp = new();
    private readonly StarterKit _kit = StarterKit.LoadFile(TestPaths.StarterKitPath, Catalog);
    private readonly PlayerStore _players;

    public PlayerStoreTests()
    {
        _players = new PlayerStore(_temp.Database, Catalog, _kit, new FixedTime(new DateTimeOffset(2026, 10, 26, 20, 0, 0, TimeSpan.Zero)));
    }

    public void Dispose() => _temp.Dispose();

    private async Task MigrateAsync()
    {
        await _temp.Database.InitializeAsync();
        await Migrator.MigrateAsync(_temp.Database, Migrator.LoadEmbedded());
    }

    [Fact]
    public async Task NewPlayer_GetsTheStarterKit()
    {
        await MigrateAsync();

        var login = await _players.LoginOrCreateAsync("Sam");
        var profile = await _players.GetProfileAsync(login.PlayerId);

        Assert.True(login.Created);
        Assert.NotNull(profile);
        Assert.Equal("Sam", profile.DisplayName);
        Assert.Equal(0, profile.ProfileVersion);
        Assert.Empty(InventoryValidator.Validate(Catalog, profile.Stash));
        Assert.Empty(InventoryValidator.Validate(Catalog, profile.Character));
        Assert.Equal(_kit.ItemCount, profile.Stash.Walk().Count() + profile.Character.Walk().Count());

        var rifle = profile.Character.Children.Single(c => c.Slot == SlotNames.Primary);
        Assert.Equal(30, rifle.Children.Single().Children.Single().Quantity);
    }

    [Fact]
    public async Task NewPlayer_GetsOneLedgerEntryListingEveryGrantedItem()
    {
        await MigrateAsync();

        var login = await _players.LoginOrCreateAsync("Sam");

        var entries = (await _temp.Database.ReadAsync(c => c.QueryAsync<string>(
            "SELECT detail FROM ledger_entries WHERE player_id = @id AND kind = 'grant'", new { id = login.PlayerId }))).ToList();
        var detail = JsonDocument.Parse(Assert.Single(entries)).RootElement;
        Assert.Equal("starter_kit", detail.GetProperty("reason").GetString());
        Assert.Equal(_kit.ItemCount, detail.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task LoggingInAgain_ReturnsTheSamePlayerWithoutNewItems()
    {
        await MigrateAsync();

        var first = await _players.LoginOrCreateAsync("Sam");
        var second = await _players.LoginOrCreateAsync("Sam");

        Assert.Equal(first.PlayerId, second.PlayerId);
        Assert.False(second.Created);
        var items = await _temp.Database.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM item_instances"));
        Assert.Equal(_kit.ItemCount, items);
    }

    [Fact]
    public async Task Players_OnlySeeTheirOwnItems()
    {
        await MigrateAsync();

        var sam = await _players.LoginOrCreateAsync("Sam");
        var ann = await _players.LoginOrCreateAsync("Ann");

        var samIds = (await _players.GetProfileAsync(sam.PlayerId))!.Stash.Walk().Select(n => n.Id).ToHashSet();
        var annIds = (await _players.GetProfileAsync(ann.PlayerId))!.Stash.Walk().Select(n => n.Id).ToHashSet();
        Assert.Empty(samIds.Intersect(annIds));
    }

    [Fact]
    public async Task Profile_LeavesOutItemsThatAreNoLongerInTheStash()
    {
        await MigrateAsync();
        var login = await _players.LoginOrCreateAsync("Sam");
        var medkit = (await _players.GetProfileAsync(login.PlayerId))!.Stash.Children.First(c => c.DefId == "medkit_basic");

        await _temp.Database.WriteAsync((c, tx) => c.ExecuteAsync(
            "UPDATE item_instances SET state = @state WHERE id = @id", new { state = ItemStates.Discarded, id = medkit.Id }, transaction: tx));

        var profile = await _players.GetProfileAsync(login.PlayerId);
        Assert.DoesNotContain(profile!.Stash.Walk(), n => n.Id == medkit.Id);
    }

    [Fact]
    public async Task UnknownPlayer_HasNoProfile()
    {
        await MigrateAsync();

        Assert.Null(await _players.GetProfileAsync(Uuid7.NewId()));
    }

    [Theory]
    [InlineData("Sam", true)]
    [InlineData("sam_the-3rd", true)]
    [InlineData("abc", true)]
    [InlineData("abcdefghijklmnopqrstuvwx", true)]
    [InlineData("ab", false)]
    [InlineData("abcdefghijklmnopqrstuvwxy", false)]
    [InlineData("has space", false)]
    [InlineData("émile", false)]
    [InlineData(null, false)]
    public void DisplayNames_AreShortAndPlain(string? name, bool valid)
    {
        Assert.Equal(valid, DisplayNames.IsValid(name));
    }
}
