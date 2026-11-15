using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using RaidZero.Backend.Players;

namespace RaidZero.Backend.Auth;

/// <summary>
/// Authenticates "Authorization: Bearer &lt;player token&gt;". Endpoints that need a player use
/// RequireAuthorization() and read the ID with <see cref="PlayerClaims.GetPlayerId"/>. Swapping dev login for
/// Steam later means changing how tokens are issued, not this handler or the endpoints behind it.
///
/// A player is logged in in one place at a time, the latest: a token from an earlier login is refused with
/// 401 signed_in_elsewhere, which clients take as being logged out. That costs one read of the player's row per
/// request. A token for a player who doesn't exist is let through, for the endpoint's own player_not_found.
/// Refusals carry the usual error body.
/// </summary>
public sealed class PlayerTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "PlayerToken";

    public const string SignedInElsewhere = "signed_in_elsewhere";

    private const string RefusalKey = "player_token_refusal";

    private readonly PlayerTokens _tokens;
    private readonly PlayerStore _players;

    public PlayerTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, PlayerTokens tokens, PlayerStore players)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
        _players = players;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        const string prefix = "Bearer ";
        if (header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        // Player tokens are payload.signature. Game server and admin tokens have no dot: they're for the other
        // schemes, so leave them alone rather than logging every game server call as a failed player login.
        var token = header[prefix.Length..].Trim();
        if (!token.Contains('.'))
        {
            return AuthenticateResult.NoResult();
        }

        if (!_tokens.TryValidate(token, out var playerId, out var sessionId))
        {
            return AuthenticateResult.Fail("Invalid or expired player token.");
        }

        var current = await _players.CurrentSessionAsync(playerId, Context.RequestAborted);
        if (current is not null && current != sessionId)
        {
            Context.Items[RefusalKey] = SignedInElsewhere;
            return AuthenticateResult.Fail("The player has logged in somewhere else since.");
        }

        var identity = new ClaimsIdentity([new Claim(PlayerClaims.PlayerIdType, playerId)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        var error = Context.Items[RefusalKey] as string == SignedInElsewhere
            ? new ApiError(SignedInElsewhere, "You logged in somewhere else. Log in again here to play on this one.")
            : new ApiError("unauthorized", "Log in first, or again: the token is missing, invalid or expired.");
        await Response.WriteAsJsonAsync(error, JsonDefaults.Options);
    }
}

public static class PlayerClaims
{
    public const string PlayerIdType = "player_id";

    /// <summary>The authenticated player's ID. Only call this behind RequireAuthorization().</summary>
    public static string GetPlayerId(this ClaimsPrincipal user) =>
        user.FindFirstValue(PlayerIdType) ?? throw new InvalidOperationException("No authenticated player on this request.");
}
