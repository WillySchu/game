using System.Net.Http.Json;
using System.Text.Json;
using Game.Backend.Tests;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi.Tests;

/// <summary>The Hideout's session against the real backend.</summary>
public class PlayerSessionApiTests
{
    private static readonly ItemCatalog Catalog = CatalogJson.LoadFile(TestPaths.CatalogPath);

    private static PlayerSession Session(ApiHost api, CountingTransport? transport = null) =>
        new(api.Client.BaseAddress!.ToString(), Catalog,
            new PlayerSessionOptions { ResultPollInterval = TimeSpan.FromMilliseconds(20) },
            transport ?? new CountingTransport(api.Client.BaseAddress!.ToString()));

    /// <summary>Real HTTP, counting what actually goes out.</summary>
    private sealed class CountingTransport(string baseUrl) : IBackendTransport
    {
        private readonly HttpClientTransport _http = new(baseUrl, TimeSpan.FromSeconds(10));
        private int _sent;

        public int Sent => Volatile.Read(ref _sent);

        public Task<BackendResponse> SendAsync(BackendRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sent);
            return _http.SendAsync(request, cancellationToken);
        }

        public void Dispose() => _http.Dispose();
    }

    /// <summary>What the backend itself says the profile is, bypassing the session.</summary>
    private static async Task<string> BackendTreesAsync(ApiHost api, string name)
    {
        using var other = Session(api);
        var login = await other.DevLoginAsync(name);
        Assert.True(login.Succeeded, login.ToString());
        return Trees(login.Value);
    }

    /// <summary>Order inside a container means nothing (grid items carry x and y), so it's left out of the comparison.</summary>
    private static string Trees(PlayerProfile profile) =>
        JsonSerializer.Serialize(new { profile.Version, Stash = Sorted(profile.Stash), Character = Sorted(profile.Character) });

    private static ItemNode Sorted(ItemNode node)
    {
        var copy = node.Clone();
        copy.Children = copy.Children.Select(Sorted).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        return copy;
    }

    private static ItemNode StashItem(PlayerSession session, string defId) =>
        session.Profile!.Stash.Children.First(c => c.DefId == defId);

    [Fact]
    public async Task LoggingIn_LoadsTheStarterKit()
    {
        await using var api = await ApiHost.StartAsync();
        using var session = Session(api);
        var changes = new List<PlayerProfile>();
        session.ProfileChanged += changes.Add;

        var login = await session.DevLoginAsync("Sam");

        Assert.True(login.Succeeded, login.ToString());
        Assert.True(session.IsLoggedIn);
        Assert.Equal(("Sam", 0L, false), (login.Value.DisplayName, login.Value.Version, login.Value.IsInRaid));
        Assert.Equal("rifle_ak_basic", login.Value.Character.Children.Single(c => c.Slot == "primary").DefId);
        Assert.NotEmpty(login.Value.Stash.Children);
        Assert.Same(login.Value, Assert.Single(changes));
    }

    [Fact]
    public async Task Edits_AreAppliedLocally_ExactlyAsTheBackendAppliesThem()
    {
        await using var api = await ApiHost.StartAsync();
        using var session = Session(api);
        await session.DevLoginAsync("Sam");
        var ammo = session.Profile!.Stash.Children.Where(c => c.DefId == "ammo_545_ps").ToList();
        var stash = session.Profile.Stash.Id;

        // Split one stack (a new ID the backend chooses), merge the other into it, and move the medkit.
        var result = await session.ApplyAsync(
        [
            InventoryOp.Split(ammo[0].Id, 25, InventoryTarget.Grid(stash, 9, 29)),
            InventoryOp.Merge(ammo[1].Id, ammo[0].Id, 10),
            InventoryOp.Move(StashItem(session, "medkit_basic").Id, InventoryTarget.Grid(stash, 8, 29)),
        ]);

        Assert.True(result.Succeeded, result.ToString());
        var split = Assert.Single(result.Created);
        Assert.Equal(0, split.OpIndex);
        Assert.Contains(session.Profile.Stash.Children, c => c.Id == split.ItemId && c.Quantity == 25 && c.X == 9 && c.Y == 29);
        Assert.Equal(1L, session.Profile.Version);
        Assert.Equal(await BackendTreesAsync(api, "Sam"), Trees(session.Profile));
    }

    [Fact]
    public async Task AnEditTheRulesRefuse_NeverReachesTheBackend()
    {
        await using var api = await ApiHost.StartAsync();
        var transport = new CountingTransport(api.Client.BaseAddress!.ToString());
        using var session = Session(api, transport);
        await session.DevLoginAsync("Sam");
        var sent = transport.Sent;
        var first = session.Profile!.Stash.Children[0];
        var second = session.Profile.Stash.Children[1];

        var result = await session.ApplyAsync([InventoryOp.Move(second.Id, InventoryTarget.Grid(session.Profile.Stash.Id, first.X!.Value, first.Y!.Value))]);

        Assert.Equal((InventoryErrorCodes.Overlap, 0), (result.Error, result.FailedOpIndex!.Value));
        Assert.Equal(sent, transport.Sent);
    }

    [Fact]
    public async Task AStaleEdit_IsRefreshedAndSentAgain_WhenItStillFits()
    {
        await using var api = await ApiHost.StartAsync();
        using var desktop = Session(api);
        using var laptop = Session(api);
        await desktop.DevLoginAsync("Sam");
        await laptop.DevLoginAsync("Sam");

        // The desktop moves the medkit; the laptop, still on the old version, moves the pistol.
        await desktop.ApplyAsync([InventoryOp.Move(StashItem(desktop, "medkit_basic").Id, InventoryTarget.Grid(desktop.Profile!.Stash.Id, 9, 29))]);
        var result = await laptop.ApplyAsync([InventoryOp.Move(StashItem(laptop, "pistol_pm").Id, InventoryTarget.Grid(laptop.Profile!.Stash.Id, 0, 27))]);

        Assert.True(result.Succeeded, result.ToString());
        Assert.True(result.Retried);
        Assert.Equal(2L, laptop.Profile.Version);
        Assert.Contains(laptop.Profile.Stash.Children, c => c.DefId == "medkit_basic" && c.X == 9 && c.Y == 29);
        Assert.Equal(await BackendTreesAsync(api, "Sam"), Trees(laptop.Profile));
    }

    [Fact]
    public async Task AStaleEdit_ThatNoLongerFits_IsReported_WithTheStashRefreshed()
    {
        await using var api = await ApiHost.StartAsync();
        using var desktop = Session(api);
        using var laptop = Session(api);
        await desktop.DevLoginAsync("Sam");
        await laptop.DevLoginAsync("Sam");

        // Both want the same empty cell for different items; the desktop gets there first.
        await desktop.ApplyAsync([InventoryOp.Move(StashItem(desktop, "medkit_basic").Id, InventoryTarget.Grid(desktop.Profile!.Stash.Id, 9, 29))]);
        var result = await laptop.ApplyAsync([InventoryOp.Move(StashItem(laptop, "currency_credits").Id, InventoryTarget.Grid(laptop.Profile!.Stash.Id, 9, 29))]);

        Assert.Equal("version_conflict", result.Error);
        Assert.True(result.Retried);
        Assert.Contains(laptop.Profile.Stash.Children, c => c.DefId == "medkit_basic" && c.X == 9 && c.Y == 29);
        Assert.Equal(1L, laptop.Profile.Version);
    }

    [Fact]
    public async Task Deploying_LocksTheStash_AndTheResultArrivesAfterTheRaid()
    {
        await using var api = await ApiHost.StartAsync();
        using var session = Session(api);
        await session.DevLoginAsync("Sam");
        var catalog = (await (await api.Client.GetAsync("/healthz")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("catalog_version").GetString()!;

        var ticket = await session.RequestTicketAsync("greybox_01");
        Assert.True(ticket.Succeeded, ticket.ToString());
        Assert.Equal(ticket.Value.RaidId, session.Profile!.ActiveRaidId);
        Assert.True(ticket.Value.ExpiresAt > DateTimeOffset.UtcNow);

        var locked = await session.ApplyAsync([InventoryOp.Move(StashItem(session, "medkit_basic").Id, InventoryTarget.Grid(session.Profile.Stash.Id, 9, 29))]);
        Assert.Equal(("in_raid", ticket.Value.RaidId), (locked.Error, locked.RaidId));
        var again = await session.RequestTicketAsync("greybox_01");
        Assert.Equal(("already_in_raid", ticket.Value.RaidId), (again.Error, again.RaidId));

        // The game server lets them in, and they die.
        var outbox = Path.Combine(Path.GetTempPath(), "raidzero-tests", "outbox-" + Guid.NewGuid().ToString("N"));
        using (var server = new BackendClient(new BackendSettings(api.Client.BaseAddress!.ToString(), ApiHost.ServerToken, outbox), catalog,
                   new BackendClientOptions(), new HttpClientTransport(api.Client.BaseAddress!.ToString(), TimeSpan.FromSeconds(10)), new CapturingLog()))
        {
            server.Start();
            var joined = await server.JoinAsync(ticket.Value.RaidId, ticket.Value.JoinToken);
            Assert.True(joined.Succeeded, joined.ToString());
            var pending = await session.GetRaidResultAsync(ticket.Value.RaidId);
            Assert.False(pending.Value.IsFinished);

            server.ReportDied(joined.PlayerId);
            var result = await session.WaitForRaidResultAsync(ticket.Value.RaidId, TimeSpan.FromSeconds(10));

            Assert.True(result.Succeeded, result.ToString());
            Assert.Equal("died", result.Value.Outcome);
            Assert.NotEmpty(result.Value.ItemsLost);
        }

        Assert.False(session.Profile.IsInRaid);
        Assert.Equal(ticket.Value.RaidId, session.Profile.LastRaidId);
        Assert.Empty(session.Profile.Character.Children);
        Directory.Delete(outbox, recursive: true);
    }
}
