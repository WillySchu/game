using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RaidZero.Backend.Admin;
using RaidZero.Backend.Auth;
using RaidZero.Backend.Catalog;
using RaidZero.Backend.Data;
using RaidZero.Backend.Players;
using RaidZero.Backend.Raids;
using RaidZero.Shared.Catalog;

namespace RaidZero.Backend;

/// <summary>
/// Builds the backend. Program.cs calls this, and so do the API tests, which host the real app on a
/// random local port and talk to it over HTTP, with no test-only package in between.
/// </summary>
public static class BackendApp
{
    /// <param name="configure">Runs before services are registered; tests use it to override settings and the clock.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        var section = builder.Configuration.GetSection(BackendOptions.SectionName);
        builder.Services.Configure<BackendOptions>(section);
        var options = section.Get<BackendOptions>() ?? new BackendOptions();
        string Resolve(string path) => Path.GetFullPath(path, builder.Environment.ContentRootPath);

        builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Configure(o.SerializerOptions));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(_ => new Database(Resolve(options.DatabasePath)));
        builder.Services.AddSingleton(_ => CatalogLoader.LoadFile(Resolve(options.CatalogPath)));
        builder.Services.AddSingleton(sp => StarterKit.LoadFile(Resolve(options.StarterKitPath), sp.GetRequiredService<ItemCatalog>()));
        builder.Services.AddSingleton(sp => PlayerTokens.FromOptions(options, sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<PlayerStore>();
        builder.Services.AddSingleton<StashInventory>();
        builder.Services.AddSingleton(_ => new GameServerRegistry(options.Servers));
        builder.Services.AddSingleton<RaidService>();
        builder.Services.AddHostedService<RaidReaper>();
        builder.Services.AddSingleton<AdminService>();

        builder.Services
            .AddAuthentication(PlayerTokenHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PlayerTokenHandler>(PlayerTokenHandler.SchemeName, null)
            .AddScheme<AuthenticationSchemeOptions, ServerTokenHandler>(ServerTokenHandler.SchemeName, null)
            .AddScheme<AuthenticationSchemeOptions, AdminTokenHandler>(AdminTokenHandler.SchemeName, null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy(Policies.Player, p => p.AddAuthenticationSchemes(PlayerTokenHandler.SchemeName).RequireClaim(PlayerClaims.PlayerIdType));
            o.AddPolicy(Policies.GameServer, p => p.AddAuthenticationSchemes(ServerTokenHandler.SchemeName).RequireClaim(ServerClaims.ServerIdType));
            o.AddPolicy(Policies.Admin, p => p.AddAuthenticationSchemes(AdminTokenHandler.SchemeName).RequireClaim(AdminTokenHandler.ClaimType));
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/healthz", async (Database db, ItemCatalog items, CancellationToken ct) =>
        {
            var schema = await db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(version), 0) FROM schema_migrations"), ct);
            return Results.Ok(new { Status = "ok", CatalogVersion = items.Version, SchemaVersion = schema });
        });
        app.MapPlayerEndpoints(options);
        app.MapRaidEndpoints();
        app.MapAdminEndpoints(options);

        return app;
    }

    /// <summary>
    /// Fails fast before accepting traffic: a bad catalog, a kit that no longer fits it, a missing signing
    /// key or an unexpected schema all stop startup here, with a message saying what to fix.
    /// </summary>
    public static async Task InitializeAsync(WebApplication app, CancellationToken ct = default)
    {
        var catalog = app.Services.GetRequiredService<ItemCatalog>();
        var kit = app.Services.GetRequiredService<StarterKit>();
        app.Services.GetRequiredService<PlayerTokens>();
        var servers = app.Services.GetRequiredService<GameServerRegistry>();

        var database = app.Services.GetRequiredService<Database>();
        await database.InitializeAsync(ct);
        var schemaVersion = await Migrator.MigrateAsync(database, Migrator.LoadEmbedded(), app.Logger, ct);

        var devLogin = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackendOptions>>().Value.DevLoginEnabled;
        app.Logger.LogInformation(
            "Ready: {ItemCount} item definitions (catalog {CatalogVersion}), starter kit of {KitItems} items, schema version {SchemaVersion}, {ServerCount} game server(s), dev login {DevLogin}, database {DatabasePath}",
            catalog.Count, catalog.Version, kit.ItemCount, schemaVersion, servers.All.Count, devLogin ? "ON" : "off", database.FilePath);
        var adminToken = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackendOptions>>().Value.AdminToken;
        if (adminToken.Length is > 0 and < AdminTokenHandler.MinTokenLength)
        {
            throw new InvalidOperationException($"Backend:AdminToken must be at least {AdminTokenHandler.MinTokenLength} characters (openssl rand -base64 32).");
        }

        app.Logger.LogInformation(adminToken.Length > 0 ? "Admin tool is on at /admin." : "Admin tool is off (Backend:AdminToken isn't set).");
        if (servers.All.Count == 0)
        {
            app.Logger.LogWarning("No game servers are configured (Backend:Servers), so raid tickets will fail with no_server_available.");
        }
    }
}
