using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Backend.Tests;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi.Tests;

/// <summary>Whole raids through the client against the real backend, over real HTTP.</summary>
public class BackendClientApiTests
{
    private static BackendClientOptions Fast() => new()
    {
        HeartbeatInterval = TimeSpan.FromMilliseconds(50),
        RetryInitialDelay = TimeSpan.FromMilliseconds(10),
        RetryMaxDelay = TimeSpan.FromMilliseconds(50),
    };

    private static async Task<string> CatalogVersionAsync(ApiHost api) =>
        (await (await api.Client.GetAsync("/healthz")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("catalog_version").GetString()!;

    private static async Task<(string RaidId, string JoinToken)> TicketAsync(ApiHost api, string playerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/me/raid-tickets") { Content = JsonContent.Create(new { map_id = "greybox_01" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", playerToken);
        using var response = await api.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("raid_id").GetString()!, body.GetProperty("join_token").GetString()!);
    }

    private static async Task<JsonElement> GetAsync(ApiHost api, string path, string playerToken)
    {
        using var response = await api.GetAsync(path, playerToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static BackendClient Client(ApiHost api, string catalog, TempFolder outbox, IBackendTransport transport, string? mapId = null)
    {
        var options = Fast();
        options.MapId = mapId;
        return new(new BackendSettings(api.Client.BaseAddress!.ToString(), ApiHost.ServerToken, outbox.Path), catalog, options, transport, new CapturingLog());
    }

    /// <summary>The real HTTP transport, plus a switch that throws away chosen answers after the backend has acted.</summary>
    private sealed class LossyTransport(string baseUrl) : IBackendTransport
    {
        private readonly HttpClientTransport _http = new(baseUrl, TimeSpan.FromSeconds(10));
        private int _dropped;

        public Func<BackendRequest, bool> DropAnswer { get; set; } = _ => false;

        public int Dropped => Volatile.Read(ref _dropped);

        public async Task<BackendResponse> SendAsync(BackendRequest request, CancellationToken cancellationToken)
        {
            var response = await _http.SendAsync(request, cancellationToken);
            if (DropAnswer(request))
            {
                Interlocked.Increment(ref _dropped);
                throw new HttpRequestException("The connection dropped before the answer arrived.");
            }

            return response;
        }

        public void Dispose() => _http.Dispose();
    }

    [Fact]
    public async Task TheWholeRaid_ThroughTheClient()
    {
        await using var api = await ApiHost.StartAsync();
        using var outbox = new TempFolder();
        var (playerId, playerToken) = await api.LoginAsync();
        var (raidId, joinToken) = await TicketAsync(api, playerToken);
        using var transport = new HttpClientTransport(api.Client.BaseAddress!.ToString(), TimeSpan.FromSeconds(10));
        using var client = Client(api, await CatalogVersionAsync(api), outbox, transport);
        client.Start();

        var joined = await client.JoinAsync(raidId, joinToken);
        Assert.True(joined.Succeeded, joined.ToString());
        Assert.Equal(playerId, joined.PlayerId);
        Assert.Equal("character", joined.Loadout.DefId);

        // A graphics card picked up in the raid goes in the backpack, then the player extracts.
        var gpu = Uuid7.NewId();
        joined.Loadout.Children.Single(c => c.Slot == SlotNames.Backpack).Children
            .Add(new ItemNode { Id = gpu, DefId = "loot_gpu", Slot = SlotNames.Grid, X = 0, Y = 0 });
        client.ReportExtracted(joined.PlayerId, joined.Loadout, "north_gate");
        await Wait.ForAsync(client.EndRaidAsync(), "the raid to end");

        var result = await GetAsync(api, $"/v1/me/raids/{raidId}", playerToken);
        Assert.Equal("extracted", result.GetProperty("outcome").GetString());
        Assert.Equal(gpu, Assert.Single(result.GetProperty("items_gained").EnumerateArray()).GetString());
        var profile = await GetAsync(api, "/v1/me/profile", playerToken);
        Assert.Contains(gpu, profile.GetProperty("character").GetRawText());
        Assert.Empty(outbox.Journals());
    }

    [Fact]
    public async Task LostAnswers_AreReplayed_NeverAppliedTwice()
    {
        await using var api = await ApiHost.StartAsync();
        using var outbox = new TempFolder();
        var (_, playerToken) = await api.LoginAsync();
        var (raidId, joinToken) = await TicketAsync(api, playerToken);
        using var transport = new LossyTransport(api.Client.BaseAddress!.ToString());
        using var client = Client(api, await CatalogVersionAsync(api), outbox, transport);
        client.Start();

        // The first answer to the join and to each report is lost after the backend has committed it.
        var seen = new HashSet<string>();
        transport.DropAnswer = request => !request.Path.EndsWith("/heartbeat", StringComparison.Ordinal) && seen.Add(request.Path);

        var joined = await client.JoinAsync(raidId, joinToken);
        Assert.True(joined.Succeeded, joined.ToString());

        var gpu = Uuid7.NewId();
        joined.Loadout.Children.Single(c => c.Slot == SlotNames.Backpack).Children
            .Add(new ItemNode { Id = gpu, DefId = "loot_gpu", Slot = SlotNames.Grid, X = 0, Y = 0 });
        client.ReportExtracted(joined.PlayerId, joined.Loadout);
        await Wait.ForAsync(client.EndRaidAsync(), "the raid to end");

        Assert.Equal(3, transport.Dropped);
        var result = await GetAsync(api, $"/v1/me/raids/{raidId}", playerToken);
        Assert.Equal("extracted", result.GetProperty("outcome").GetString());
        Assert.Equal(gpu, Assert.Single(result.GetProperty("items_gained").EnumerateArray()).GetString());
        var character = (await GetAsync(api, "/v1/me/profile", playerToken)).GetProperty("character").GetRawText();
        Assert.Single(character.Split(gpu).Skip(1));
        Assert.Empty(outbox.Journals());
    }

    [Fact]
    public async Task ADeath_LeavesTheGearToTheRaid()
    {
        await using var api = await ApiHost.StartAsync();
        using var outbox = new TempFolder();
        var (_, playerToken) = await api.LoginAsync();
        var (raidId, joinToken) = await TicketAsync(api, playerToken);
        using var transport = new HttpClientTransport(api.Client.BaseAddress!.ToString(), TimeSpan.FromSeconds(10));
        using var client = Client(api, await CatalogVersionAsync(api), outbox, transport);
        client.Start();

        var joined = await client.JoinAsync(raidId, joinToken);
        client.ReportDied(joined.PlayerId);
        await Wait.ForAsync(client.EndRaidAsync(), "the raid to end");

        var result = await GetAsync(api, $"/v1/me/raids/{raidId}", playerToken);
        Assert.Equal("died", result.GetProperty("outcome").GetString());
        var character = (await GetAsync(api, "/v1/me/profile", playerToken)).GetProperty("character");
        Assert.Empty(character.GetProperty("children").EnumerateArray());
    }

    [Fact]
    public async Task AServerBuiltFromAnotherCatalog_IsTurnedAwayAtTheDoor()
    {
        await using var api = await ApiHost.StartAsync();
        using var outbox = new TempFolder();
        var (_, playerToken) = await api.LoginAsync();
        var (raidId, joinToken) = await TicketAsync(api, playerToken);
        using var transport = new HttpClientTransport(api.Client.BaseAddress!.ToString(), TimeSpan.FromSeconds(10));
        using var client = Client(api, "0000000000000000", outbox, transport);
        client.Start();

        var joined = await client.JoinAsync(raidId, joinToken);

        // Retried (someone may be mid-deploy), then refused, and the player's gear never left home.
        Assert.Equal("catalog_mismatch", joined.Error);
        var character = (await GetAsync(api, "/v1/me/profile", playerToken)).GetProperty("character");
        Assert.NotEmpty(character.GetProperty("children").EnumerateArray());
    }

    [Fact]
    public async Task AServerWithAnotherMapLoaded_IsTurnedAwayAtTheDoor()
    {
        await using var api = await ApiHost.StartAsync();
        using var outbox = new TempFolder();
        var (_, playerToken) = await api.LoginAsync();
        var (raidId, joinToken) = await TicketAsync(api, playerToken);
        using var transport = new HttpClientTransport(api.Client.BaseAddress!.ToString(), TimeSpan.FromSeconds(10));
        var catalog = await CatalogVersionAsync(api);

        using (var wrong = Client(api, catalog, outbox, transport, mapId: "other_map"))
        {
            wrong.Start();
            Assert.Equal("wrong_map", (await wrong.JoinAsync(raidId, joinToken)).Error);
        }

        var character = (await GetAsync(api, "/v1/me/profile", playerToken)).GetProperty("character");
        Assert.NotEmpty(character.GetProperty("children").EnumerateArray());

        using var right = Client(api, catalog, outbox, transport, mapId: "greybox_01");
        right.Start();
        var joined = await right.JoinAsync(raidId, joinToken);
        Assert.True(joined.Succeeded, joined.ToString());
    }
}
