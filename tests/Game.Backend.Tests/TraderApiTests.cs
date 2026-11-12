using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Game.Backend.Tests;

/// <summary>/v1/me/traders over HTTP: the shapes and status codes the Hideout relies on.</summary>
public class TraderApiTests
{
    private static Task<HttpResponseMessage> PostAsync(ApiHost api, string path, string? token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return api.Client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> ProfileAsync(ApiHost api, string token) => await JsonAsync(await api.GetAsync("/v1/me/profile", token));

    [Fact]
    public async Task Traders_ListTheirOffers_WithWhatsLeft()
    {
        await using var api = await ApiHost.StartAsync();
        var (_, token) = await api.LoginAsync();

        var response = await api.GetAsync("/v1/me/traders", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var store = Assert.Single((await JsonAsync(response)).GetProperty("traders").EnumerateArray().ToList());
        Assert.Equal("general_store", store.GetProperty("trader_id").GetString());
        Assert.Equal(0.5, store.GetProperty("buys_at").GetDouble());
        var pistol = store.GetProperty("offers").EnumerateArray().Single(o => o.GetProperty("offer_id").GetString() == "pistol_pm");
        Assert.Equal(14000, pistol.GetProperty("price").GetInt64());
        Assert.Equal(2, pistol.GetProperty("stock").GetInt32());
        Assert.Equal(2, pistol.GetProperty("left").GetInt32());
        Assert.True(pistol.TryGetProperty("restocks_at", out _));
        Assert.Equal("pistol_pm", pistol.GetProperty("item").GetProperty("def_id").GetString());
        Assert.False(pistol.GetProperty("item").TryGetProperty("id", out _)); // a spec, not an item
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/v1/me/traders", null)).StatusCode);
    }

    [Fact]
    public async Task BuyAndSell_RoundTrip()
    {
        await using var api = await ApiHost.StartAsync();
        var (_, token) = await api.LoginAsync();

        var buy = await PostAsync(api, "/v1/me/traders/general_store/buy", token, new { expected_version = 0, offer_id = "medkit_basic", count = 2 });

        Assert.Equal(HttpStatusCode.OK, buy.StatusCode);
        var bought = await JsonAsync(buy);
        Assert.Equal(1, bought.GetProperty("profile_version").GetInt64());
        Assert.Equal(9000, bought.GetProperty("price").GetInt64());
        var created = bought.GetProperty("created").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(2, created.Count);

        var sell = await PostAsync(api, "/v1/me/traders/general_store/sell", token, new { expected_version = 1, item_ids = created });

        Assert.Equal(HttpStatusCode.OK, sell.StatusCode);
        var sold = await JsonAsync(sell);
        Assert.Equal(2, sold.GetProperty("profile_version").GetInt64());
        Assert.Equal(3000, sold.GetProperty("credits").GetInt64());
        Assert.Equal(created, sold.GetProperty("sold").EnumerateArray().Select(e => e.GetString()!));
        var profile = await ProfileAsync(api, token);
        var money = profile.GetProperty("stash").GetProperty("children").EnumerateArray().Single(c => c.GetProperty("def_id").GetString() == "currency_credits");
        Assert.Equal(50000 - 9000 + 3000, money.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task Refusals_SayWhy()
    {
        await using var api = await ApiHost.StartAsync();
        var (_, token) = await api.LoginAsync();

        var broke = await PostAsync(api, "/v1/me/traders/general_store/buy", token, new { expected_version = 0, offer_id = "rifle_ak_basic", count = 2 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, broke.StatusCode);
        var error = await JsonAsync(broke);
        Assert.Equal("invalid_trade", error.GetProperty("error").GetString());
        Assert.Equal("out_of_stock", error.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));

        var stale = await PostAsync(api, "/v1/me/traders/general_store/buy", token, new { expected_version = 3, offer_id = "medkit_basic" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("version_conflict", (await JsonAsync(stale)).GetProperty("error").GetString());

        var nobody = await PostAsync(api, "/v1/me/traders/black_market/buy", token, new { expected_version = 0, offer_id = "medkit_basic" });
        Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
        Assert.Equal("unknown_trader", (await JsonAsync(nobody)).GetProperty("error").GetString());

        var nothing = await PostAsync(api, "/v1/me/traders/general_store/sell", token, new { expected_version = 0, item_ids = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nothing.StatusCode);
        Assert.Equal("invalid_request", (await JsonAsync(nothing)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task WithARaidTicket_TheStashIsLocked()
    {
        await using var api = await ApiHost.StartAsync();
        var (_, token) = await api.LoginAsync();
        var ticket = await PostAsync(api, "/v1/me/raid-tickets", token, new { map_id = "greybox_01" });
        Assert.Equal(HttpStatusCode.OK, ticket.StatusCode);

        var buy = await PostAsync(api, "/v1/me/traders/general_store/buy", token, new { expected_version = 0, offer_id = "medkit_basic" });

        Assert.Equal(HttpStatusCode.Conflict, buy.StatusCode);
        Assert.Equal("in_raid", (await JsonAsync(buy)).GetProperty("error").GetString());
    }
}
