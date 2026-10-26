using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace RaidZero.Backend.Auth;

/// <summary>
/// Authenticates "Authorization: Bearer &lt;player token&gt;". Endpoints that need a player use
/// RequireAuthorization() and read the ID with <see cref="PlayerClaims.GetPlayerId"/>. Swapping dev login for
/// Steam later means changing how tokens are issued, not this handler or the endpoints behind it.
/// </summary>
public sealed class PlayerTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "PlayerToken";

    private readonly PlayerTokens _tokens;

    public PlayerTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, PlayerTokens tokens)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        const string prefix = "Bearer ";
        if (header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!_tokens.TryValidate(header[prefix.Length..].Trim(), out var playerId))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired player token."));
        }

        var identity = new ClaimsIdentity([new Claim(PlayerClaims.PlayerIdType, playerId)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public static class PlayerClaims
{
    public const string PlayerIdType = "player_id";

    /// <summary>The authenticated player's ID. Only call this behind RequireAuthorization().</summary>
    public static string GetPlayerId(this ClaimsPrincipal user) =>
        user.FindFirstValue(PlayerIdType) ?? throw new InvalidOperationException("No authenticated player on this request.");
}
