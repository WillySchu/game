using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using RaidZero.Backend;

namespace Game.Backend.Tests;

/// <summary>The real app on a random local port, with its own database, spoken to over HTTP.</summary>
public sealed class ApiHost : IAsyncDisposable
{
    public static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public const string ServerId = "dev-local";

    public static readonly string ServerToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static readonly string AdminToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly WebApplication _app;
    private readonly string _folder;

    private ApiHost(WebApplication app, string folder, HttpClient client)
    {
        _app = app;
        _folder = folder;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<ApiHost> StartAsync(bool devLogin = true, bool admin = true, int backupsToKeep = 14)
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
                ["Backend:Servers:0:Id"] = ServerId,
                ["Backend:Servers:0:Token"] = ServerToken,
                ["Backend:AdminToken"] = admin ? AdminToken : "",
                ["Backend:BackupsToKeep"] = backupsToKeep.ToString(),
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
