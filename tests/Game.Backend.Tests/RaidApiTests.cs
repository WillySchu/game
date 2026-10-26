using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using RaidZero.Shared.Ids;

namespace Game.Backend.Tests;

/// <summary>The raid loop over HTTP, with player and game server credentials exactly as the real clients send them.</summary>
public class RaidApiTests
{
    private static HttpRequestMessage Request(HttpMethod method, string path, string? token, object? body = null, string? idempotencyKey = null, string? catalogVersion = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = body is string json ? new StringContent(json, System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (catalogVersion is not null)
        {
            request.Headers.Add("X-Catalog-Version", catalogVersion);
        }

        return request;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(ApiHost api, HttpRequestMessage request)
    {
        var response = await api.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
    }

    private static async Task<string> CatalogVersionAsync(ApiHost api) =>
        (await (await api.Client.GetAsync("/healthz")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("catalog_version").GetString()!;

    private static async Task<(string RaidId, string JoinToken)> TicketAsync(ApiHost api, string playerToken)
    {
        var (status, body) = await SendAsync(api, Request(HttpMethod.Post, "/v1/me/raid-tickets", playerToken, new { map_id = "greybox_01" }));
        Assert.Equal(HttpStatusCode.OK, status);
        return (body.GetProperty("raid_id").GetString()!, body.GetProperty("join_token").GetString()!);
    }

    [Fact]
    public async Task TheWholeRaidLoop_OverHttp()
    {
        await using var api = await ApiHost.StartAsync();
        var (playerId, token) = await api.LoginAsync();
        var catalog = await CatalogVersionAsync(api);

        // Ticket, and the stash locks.
        var (raidId, joinToken) = await TicketAsync(api, token);
        var locked = await SendAsync(api, Request(HttpMethod.Post, "/v1/me/inventory/ops", token,
            new { expected_version = 0, ops = new object[] { new { op = "discard", item_id = Uuid7.NewId() } } }));
        Assert.Equal(HttpStatusCode.Conflict, locked.Status);
        Assert.Equal("in_raid", locked.Body.GetProperty("error").GetString());

        // The game server redeems the token and spawns the player with their loadout.
        var (joinStatus, joined) = await SendAsync(api, Request(HttpMethod.Post, $"/v1/server/raids/{raidId}/join", ApiHost.ServerToken,
            new { join_token = joinToken }, idempotencyKey: $"join-{raidId}-1", catalogVersion: catalog));
        Assert.Equal(HttpStatusCode.OK, joinStatus);
        Assert.Equal(playerId, joined.GetProperty("player_id").GetString());

        var (heartbeat, _) = await SendAsync(api, Request(HttpMethod.Post, $"/v1/server/raids/{raidId}/heartbeat", ApiHost.ServerToken, catalogVersion: catalog));
        Assert.Equal(HttpStatusCode.OK, heartbeat);

        // They pick up a graphics card and extract.
        var inventory = JsonNode.Parse(joined.GetProperty("loadout").GetRawText())!.AsObject();
        var backpack = inventory["children"]!.AsArray().Single(c => (string?)c!["slot"] == "backpack")!;
        var gpuId = Uuid7.NewId();
        backpack["children"]!.AsArray().Add(new JsonObject { ["id"] = gpuId, ["def_id"] = "loot_gpu", ["slot"] = "grid", ["x"] = 0, ["y"] = 0 });
        var outcome = new JsonObject { ["player_id"] = playerId, ["outcome"] = "extracted", ["extract_point"] = "north_gate", ["inventory"] = inventory }.ToJsonString();

        var (extractStatus, extracted) = await SendAsync(api, Request(HttpMethod.Post, $"/v1/server/raids/{raidId}/outcomes", ApiHost.ServerToken,
            outcome, idempotencyKey: $"outcome-{raidId}-{playerId}", catalogVersion: catalog));
        Assert.Equal(HttpStatusCode.OK, extractStatus);
        Assert.Equal(1, extracted.GetProperty("items_gained").GetInt32());

        // A retry after a lost response replays the same answer.
        var (_, replayed) = await SendAsync(api, Request(HttpMethod.Post, $"/v1/server/raids/{raidId}/outcomes", ApiHost.ServerToken,
            outcome, idempotencyKey: $"outcome-{raidId}-{playerId}", catalogVersion: catalog));
        Assert.Equal(extracted.GetRawText(), replayed.GetRawText());

        var (closeStatus, _) = await SendAsync(api, Request(HttpMethod.Post, $"/v1/server/raids/{raidId}/close", ApiHost.ServerToken,
            idempotencyKey: $"close-{raidId}", catalogVersion: catalog));
        Assert.Equal(HttpStatusCode.OK, closeStatus);

        // The player sees the result and the loot.
        var (_, result) = await SendAsync(api, Request(HttpMethod.Get, $"/v1/me/raids/{raidId}", token));
        Assert.Equal("extracted", result.GetProperty("outcome").GetString());
        Assert.Equal(gpuId, result.GetProperty("items_gained")[0].GetString());
        var profile = await (await api.GetAsync("/v1/me/profile", token)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(gpuId, profile.GetProperty("character").GetRawText());
    }

    [Fact]
    public async Task ServerEndpoints_RefusePlayerTokensAndUnknownServers()
    {
        await using var api = await ApiHost.StartAsync();
        var (_, playerToken) = await api.LoginAsync();
        var catalog = await CatalogVersionAsync(api);
        var (raidId, joinToken) = await TicketAsync(api, playerToken);

        foreach (var token in new[] { playerToken, Convert.ToBase64String(new byte[32]) })
        {
            var (status, _) = await SendAsync(api, Request(HttpMethod.Post, $"/v1/server/raids/{raidId}/join", token,
                new { join_token = joinToken }, idempotencyKey: "k", catalogVersion: catalog));
            Assert.Equal(HttpStatusCode.Unauthorized, status);
        }
    }

    [Fact]
    public async Task PlayerEndpoints_RefuseServerTokens()
    {
        await using var api = await ApiHost.StartAsync();

        var response = await api.GetAsync("/v1/me/profile", ApiHost.ServerToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ServerCalls_FromAnOlderBuild_AreRefused()
    {
        await using var api = await ApiHost.StartAsync();
        var (_, playerToken) = await api.LoginAsync();
        var (raidId, joinToken) = await TicketAsync(api, playerToken);

        var (status, body) = await SendAsync(api, Request(HttpMethod.Post, $"/v1/server/raids/{raidId}/join", ApiHost.ServerToken,
            new { join_token = joinToken }, idempotencyKey: "k", catalogVersion: "0000000000000000"));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("catalog_mismatch", body.GetProperty("error").GetString());
        Assert.Equal(await CatalogVersionAsync(api), body.GetProperty("expected").GetString());
    }

    [Fact]
    public async Task ServerCalls_ThatChangeThings_NeedAnIdempotencyKey()
    {
        await using var api = await ApiHost.StartAsync();
        var (_, playerToken) = await api.LoginAsync();
        var (raidId, joinToken) = await TicketAsync(api, playerToken);

        var (status, body) = await SendAsync(api, Request(HttpMethod.Post, $"/v1/server/raids/{raidId}/join", ApiHost.ServerToken,
            new { join_token = joinToken }, catalogVersion: await CatalogVersionAsync(api)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("missing_idempotency_key", body.GetProperty("error").GetString());
    }
}
