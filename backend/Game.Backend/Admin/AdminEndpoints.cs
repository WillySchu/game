using System.Reflection;
using RaidZero.Backend.Auth;
using RaidZero.Backend.Raids;

namespace RaidZero.Backend.Admin;

public static class AdminEndpoints
{
    /// <summary>
    /// Maps /admin (the page) and /v1/admin (its API). When Backend:AdminToken isn't set, none of it exists.
    /// </summary>
    public static void MapAdminEndpoints(this WebApplication app, BackendOptions options)
    {
        if (options.AdminToken.Length < AdminTokenHandler.MinTokenLength)
        {
            return;
        }

        // The page itself is public; it does nothing until you paste the admin token into it.
        var page = ReadPage();
        app.MapGet("/admin", () => Results.Content(page, "text/html; charset=utf-8"));

        var admin = app.MapGroup("/v1/admin").RequireAuthorization(Policies.Admin);
        admin.MapGet("/catalog", (AdminService s) => Results.Ok(new { Items = s.Catalog() }));
        admin.MapGet("/players", async (string? query, AdminService s, CancellationToken ct) => Results.Ok(new { Players = await s.SearchPlayersAsync(query, ct) }));
        admin.MapGet("/players/{playerId}", async (string playerId, AdminService s, CancellationToken ct) =>
            await s.GetPlayerAsync(playerId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound(new ApiError("player_not_found", "No such player.")));
        admin.MapPost("/players/{playerId}/grants", async (string playerId, AdminService.GrantRequest body, AdminService s, CancellationToken ct) =>
            (await s.GrantAsync(playerId, body, ct)).ToResult());
        admin.MapGet("/items/{itemId}", async (string itemId, AdminService s, CancellationToken ct) => (await s.GetItemAsync(itemId, ct)).ToResult());
        admin.MapGet("/raids", async (bool? all, AdminService s, CancellationToken ct) => (await s.ListRaidsAsync(all != true, ct)).ToResult());
        admin.MapPost("/raids/{raidId}/abort", async (string raidId, AdminService s, CancellationToken ct) => (await s.AbortRaidAsync(raidId, ct)).ToResult());
        admin.MapGet("/backups", (AdminService s) => Results.Ok(s.ListBackups()));
        admin.MapPost("/backups", async (AdminService s, CancellationToken ct) => (await s.BackupAsync(ct)).ToResult());
    }

    private static string ReadPage()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("admin/admin.html")
            ?? throw new InvalidOperationException("The admin page is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
