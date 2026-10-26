using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using RaidZero.Backend;
using RaidZero.Backend.Auth;
using RaidZero.Shared.Ids;

namespace Game.Backend.Tests;

/// <summary>The real app on a random local port, with its own database, spoken to over HTTP.</summary>
public sealed class ApiHost : IAsyncDisposable
{
    public static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly WebApplication _app;
    private readonly string _folder;

    private ApiHost(WebApplication app, string folder, HttpClient client)
    {
        _app = app;
        _folder = folder;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<ApiHost> StartAsync(bool devLogin = true)
    {
        var folder = Path.Combine(Path.GetTempPath(), "raidzero-tests", Guid.NewGuid().ToString("N"));
        var app = BackendApp.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(folder, "api.db"),
                ["Backend:CatalogPath"] = TestPaths.CatalogPath,
                ["Backend:StarterKitPath"] = TestPaths.StarterKitPath,
                ["Backend:DevLoginEnabled"] = devLogin.ToString(),
                ["Backend:TokenSigningKey"] = SigningKey,
                ["Logging:LogLevel:Default"] = "Warning",
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        });

        await BackendApp.InitializeAsync(app);
        await app.StartAsync();
        return new ApiHost(app, folder, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    public async Task<(string PlayerId, string Token)> LoginAsync(string name = "Sam")
    {
        var response = await Client.PostAsJsonAsync("/v1/auth/dev-login", new { display_name = name });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("player_id").GetString()!, body.GetProperty("token").GetString()!);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class ApiTests
{
    [Fact]
    public async Task DevLogin_CreatesThePlayerOnceAndIssuesATokenEachTime()
    {
        await using var api = await ApiHost.StartAsync();

        var first = await (await api.Client.PostAsJsonAsync("/v1/auth/dev-login", new { display_name = "Sam" })).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await api.Client.PostAsJsonAsync("/v1/auth/dev-login", new { display_name = "Sam" })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(first.GetProperty("created").GetBoolean());
        Assert.False(second.GetProperty("created").GetBoolean());
        Assert.Equal(first.GetProperty("player_id").GetString(), second.GetProperty("player_id").GetString());
        Assert.True(Uuid7.IsValid(first.GetProperty("player_id").GetString()!));
        Assert.False(string.IsNullOrEmpty(first.GetProperty("token").GetString()));
        Assert.True(first.TryGetProperty("expires_at", out _));
    }

    [Fact]
    public async Task Profile_ReturnsTheStarterKitAsItemTrees()
    {
        await using var api = await ApiHost.StartAsync();
        var (playerId, token) = await api.LoginAsync();

        var response = await api.GetAsync("/v1/me/profile", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(playerId, profile.GetProperty("player_id").GetString());
        Assert.Equal(0, profile.GetProperty("profile_version").GetInt64());

        var rifle = profile.GetProperty("character").GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("slot").GetString() == "primary");
        Assert.Equal("rifle_ak_basic", rifle.GetProperty("def_id").GetString());
        Assert.False(rifle.TryGetProperty("x", out _), "named-slot items should leave out x and y");
        var rounds = rifle.GetProperty("children")[0].GetProperty("children")[0];
        Assert.Equal(30, rounds.GetProperty("quantity").GetInt32());

        var stashItem = profile.GetProperty("stash").GetProperty("children")[0];
        Assert.True(stashItem.TryGetProperty("x", out _), "grid items should carry x and y");
    }

    [Fact]
    public async Task Profile_WithoutAToken_Is401()
    {
        await using var api = await ApiHost.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/v1/me/profile", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/v1/me/profile", "nonsense")).StatusCode);
    }

    [Fact]
    public async Task Profile_WithAnExpiredToken_Is401()
    {
        await using var api = await ApiHost.StartAsync();
        var (playerId, _) = await api.LoginAsync();
        var lastWeek = new FixedTime(DateTimeOffset.UtcNow.AddDays(-7));
        var stale = new PlayerTokens(Convert.FromBase64String(ApiHost.SigningKey), TimeSpan.FromHours(72), lastWeek).Issue(playerId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/v1/me/profile", stale.Token)).StatusCode);
    }

    [Fact]
    public async Task Profile_ForAPlayerThatDoesntExist_Is404()
    {
        await using var api = await ApiHost.StartAsync();
        var ghost = new PlayerTokens(Convert.FromBase64String(ApiHost.SigningKey), TimeSpan.FromHours(1), TimeProvider.System).Issue(Uuid7.NewId());

        var response = await api.GetAsync("/v1/me/profile", ghost.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("player_not_found", body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("")]
    public async Task DevLogin_WithABadName_Is422(string name)
    {
        await using var api = await ApiHost.StartAsync();

        var response = await api.Client.PostAsJsonAsync("/v1/auth/dev-login", new { display_name = name });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_display_name", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DevLogin_DoesNotExistWhenDisabled()
    {
        await using var api = await ApiHost.StartAsync(devLogin: false);

        var response = await api.Client.PostAsJsonAsync("/v1/auth/dev-login", new { display_name = "Sam" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReportsTheLatestSchema()
    {
        await using var api = await ApiHost.StartAsync();

        var health = await (await api.Client.GetAsync("/healthz")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.Equal(RaidZero.Backend.Data.Migrator.LoadEmbedded()[^1].Version, health.GetProperty("schema_version").GetInt32());
    }
}
