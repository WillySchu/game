using System.Security.Claims;
using RaidZero.Backend.Auth;

namespace RaidZero.Backend.Players;

public static class PlayerEndpoints
{
    public sealed record DevLoginRequest(string? DisplayName);

    public sealed record DevLoginResponse(string PlayerId, string DisplayName, bool Created, string Token, DateTimeOffset ExpiresAt);

    public static void MapPlayerEndpoints(this WebApplication app, BackendOptions options)
    {
        if (options.DevLoginEnabled)
        {
            app.MapPost("/v1/auth/dev-login", DevLoginAsync);
        }

        var me = app.MapGroup("/v1/me").RequireAuthorization();
        me.MapGet("/profile", GetProfileAsync);
    }

    private static async Task<IResult> DevLoginAsync(DevLoginRequest body, PlayerStore players, PlayerTokens tokens, CancellationToken ct)
    {
        if (!DisplayNames.IsValid(body.DisplayName))
        {
            return Results.UnprocessableEntity(new ApiError(
                "invalid_display_name",
                $"Use {DisplayNames.MinLength} to {DisplayNames.MaxLength} letters, digits, underscores or hyphens."));
        }

        var login = await players.LoginOrCreateAsync(body.DisplayName!, ct);
        var token = tokens.Issue(login.PlayerId);
        return Results.Ok(new DevLoginResponse(login.PlayerId, login.DisplayName, login.Created, token.Token, token.ExpiresAt));
    }

    private static async Task<IResult> GetProfileAsync(ClaimsPrincipal user, PlayerStore players, CancellationToken ct)
    {
        var profile = await players.GetProfileAsync(user.GetPlayerId(), ct);
        return profile is null
            ? Results.NotFound(new ApiError("player_not_found", "The player in this token no longer exists."))
            : Results.Ok(profile);
    }
}
