using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using RaidZero.Backend.Data;
using RaidZero.Shared.Ids;

namespace Game.Backend.Tests;

/// <summary>The admin tool's API over HTTP, as the /admin page uses it.</summary>
public class AdminApiTests
{
    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(ApiHost api, HttpMethod method, string path, string? token = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await api.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement : default);
    }

    private static Task<(HttpStatusCode Status, JsonElement Body)> AdminAsync(ApiHost api, HttpMethod method, string path, object? body = null) =>
        SendAsync(api, method, "/v1/admin" + path, ApiHost.AdminToken, body);

    private static async Task<JsonElement> ProfileAsync(ApiHost api, string token) =>
        await (await api.GetAsync("/v1/me/profile", token)).Content.ReadFromJsonAsync<JsonElement>();

    private static object Grant(params (string DefId, int Quantity)[] items) =>
        new { items = items.Select(i => new { def_id = i.DefId, quantity = i.Quantity }), note = "test" };

    [Fact]
    public async Task OnlyTheAdminTokenOpensTheAdminApi()
    {
        await using var api = await ApiHost.StartAsync();
        var (_, playerToken) = await api.LoginAsync();

        foreach (var token in new[] { null, playerToken, ApiHost.ServerToken, Convert.ToBase64String(new byte[32]) })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(api, HttpMethod.Get, "/v1/admin/players", token)).Status);
        }

        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(api, HttpMethod.Get, "/players")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/v1/me/profile", ApiHost.AdminToken)).StatusCode);

        var page = await api.Client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Raid Zero Admin", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithoutAnAdminToken_TheAdminToolDoesNotExist()
    {
        await using var api = await ApiHost.StartAsync(admin: false);

        Assert.Equal(HttpStatusCode.NotFound, (await api.Client.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(api, HttpMethod.Get, "/players")).Status);
    }

    [Fact]
    public async Task PlayerSearchAndDetail_ShowGearAndHistory()
    {
        await using var api = await ApiHost.StartAsync();
        var (samId, _) = await api.LoginAsync("Sam");
        await api.LoginAsync("Ann");

        var (_, found) = await AdminAsync(api, HttpMethod.Get, "/players?query=sa");
        var (_, all) = await AdminAsync(api, HttpMethod.Get, "/players");
        var (status, detail) = await AdminAsync(api, HttpMethod.Get, "/players/" + samId);

        Assert.Equal(new[] { "Sam" }, found.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("display_name").GetString()).ToArray());
        Assert.Equal(2, all.GetProperty("players").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Sam", detail.GetProperty("player").GetProperty("display_name").GetString());
        Assert.Equal(4, detail.GetProperty("character").GetProperty("children").GetArrayLength());
        Assert.Equal("grant", detail.GetProperty("ledger")[0].GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(api, HttpMethod.Get, "/players/" + Uuid7.NewId())).Status);
    }

    [Fact]
    public async Task Grants_LandInTheStashAndTellTheirStory()
    {
        await using var api = await ApiHost.StartAsync();
        var (samId, token) = await api.LoginAsync();

        var (status, granted) = await AdminAsync(api, HttpMethod.Post, $"/players/{samId}/grants", Grant(("loot_gpu", 1), ("ammo_545_ps", 30)));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, granted.GetProperty("profile_version").GetInt64());
        var gpuId = granted.GetProperty("granted")[0].GetProperty("item_id").GetString()!;

        var profile = await ProfileAsync(api, token);
        Assert.Equal(1, profile.GetProperty("profile_version").GetInt64());
        Assert.Contains(gpuId, profile.GetProperty("stash").GetRawText());

        var (_, item) = await AdminAsync(api, HttpMethod.Get, "/items/" + gpuId);
        Assert.Equal("admin", item.GetProperty("item").GetProperty("origin").GetString());
        Assert.Equal("Sam", item.GetProperty("item").GetProperty("owner_name").GetString());
        var history = item.GetProperty("history")[0];
        Assert.Equal("grant", history.GetProperty("kind").GetString());
        Assert.Equal("test", history.GetProperty("detail").GetProperty("note").GetString());
    }

    [Fact]
    public async Task Grants_ThatCantBeHonoured_GrantNothing()
    {
        await using var api = await ApiHost.StartAsync();
        var (samId, token) = await api.LoginAsync();
        var before = (await ProfileAsync(api, token)).GetProperty("stash").GetProperty("children").GetArrayLength();

        var cases = new (object Body, string Error)[]
        {
            (Grant(("plasma_rifle", 1)), "unknown_def"),
            (Grant(("stash", 1)), "unknown_def"),
            (Grant(("ammo_545_ps", 61)), "invalid_quantity"),
            (Grant(Enumerable.Repeat(("loot_fuse", 1), 51).ToArray()), "invalid_request"),
            (Grant(Enumerable.Repeat(("rifle_ak_basic", 1), 50).ToArray()), "no_space"), // 400 cells won't fit in 300
        };
        foreach (var (body, error) in cases)
        {
            var (status, response) = await AdminAsync(api, HttpMethod.Post, $"/players/{samId}/grants", body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
            Assert.Equal(error, response.GetProperty("error").GetString());
        }

        Assert.Equal(before, (await ProfileAsync(api, token)).GetProperty("stash").GetProperty("children").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(api, HttpMethod.Post, $"/players/{Uuid7.NewId()}/grants", Grant(("loot_fuse", 1)))).Status);
    }

    [Fact]
    public async Task Grants_WaitUntilThePlayerIsOutOfTheirRaid()
    {
        await using var api = await ApiHost.StartAsync();
        var (samId, token) = await api.LoginAsync();
        await SendAsync(api, HttpMethod.Post, "/v1/me/raid-tickets", token, new { map_id = "greybox_01" });

        var (status, body) = await AdminAsync(api, HttpMethod.Post, $"/players/{samId}/grants", Grant(("loot_fuse", 1)));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("in_raid", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AStuckRaid_CanBeAbortedAndRefunded()
    {
        await using var api = await ApiHost.StartAsync();
        var (samId, token) = await api.LoginAsync();
        var catalog = (await (await api.Client.GetAsync("/healthz")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("catalog_version").GetString()!;
        var (_, ticket) = await SendAsync(api, HttpMethod.Post, "/v1/me/raid-tickets", token, new { map_id = "greybox_01" });
        var raidId = ticket.GetProperty("raid_id").GetString()!;

        var join = new HttpRequestMessage(HttpMethod.Post, $"/v1/server/raids/{raidId}/join") { Content = JsonContent.Create(new { join_token = ticket.GetProperty("join_token").GetString() }) };
        join.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiHost.ServerToken);
        join.Headers.Add("Idempotency-Key", "join-1");
        join.Headers.Add("X-Catalog-Version", catalog);
        Assert.Equal(HttpStatusCode.OK, (await api.Client.SendAsync(join)).StatusCode);
        Assert.Equal(0, (await ProfileAsync(api, token)).GetProperty("character").GetProperty("children").GetArrayLength());

        var (_, live) = await AdminAsync(api, HttpMethod.Get, "/raids");
        var raid = live.GetProperty("raids").EnumerateArray().Single();
        Assert.Equal(raidId, raid.GetProperty("id").GetString());
        Assert.Equal("in_raid", raid.GetProperty("participants")[0].GetProperty("state").GetString());

        var (abortStatus, _) = await AdminAsync(api, HttpMethod.Post, $"/raids/{raidId}/abort");
        var (againStatus, again) = await AdminAsync(api, HttpMethod.Post, $"/raids/{raidId}/abort");

        Assert.Equal(HttpStatusCode.OK, abortStatus);
        Assert.Equal(4, (await ProfileAsync(api, token)).GetProperty("character").GetProperty("children").GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, againStatus);
        Assert.Equal("raid_not_active", again.GetProperty("error").GetString());
        Assert.Equal(0, (await AdminAsync(api, HttpMethod.Get, "/raids")).Body.GetProperty("raids").GetArrayLength());
        Assert.Equal(1, (await AdminAsync(api, HttpMethod.Get, "/raids?all=true")).Body.GetProperty("raids").GetArrayLength());
    }

    [Fact]
    public async Task Backups_AreRealDatabasesAndOnlyTheNewestAreKept()
    {
        await using var api = await ApiHost.StartAsync(backupsToKeep: 2);
        await api.LoginAsync("Sam");

        var first = (await AdminAsync(api, HttpMethod.Post, "/backups")).Body;
        await AdminAsync(api, HttpMethod.Post, "/backups");
        var third = (await AdminAsync(api, HttpMethod.Post, "/backups")).Body;
        var (_, list) = await AdminAsync(api, HttpMethod.Get, "/backups");

        Assert.Equal(first.GetProperty("file").GetString(), third.GetProperty("pruned")[0].GetString());
        Assert.Equal(2, list.GetProperty("backups").GetArrayLength());
        Assert.Equal(third.GetProperty("file").GetString(), list.GetProperty("backups")[0].GetProperty("file").GetString());

        var copy = new Database(Path.Combine(list.GetProperty("folder").GetString()!, third.GetProperty("file").GetString()!));
        Assert.Equal(1, await copy.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM players")));
        Assert.Equal("ok", await copy.ReadAsync(c => c.ExecuteScalarAsync<string>("PRAGMA integrity_check")));
    }

    [Fact]
    public async Task ItemLookup_ForAnUnknownItem_Is404()
    {
        await using var api = await ApiHost.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(api, HttpMethod.Get, "/items/" + Uuid7.NewId())).Status);
    }
}
