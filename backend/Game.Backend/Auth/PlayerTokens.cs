using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RaidZero.Shared.Ids;

namespace RaidZero.Backend.Auth;

public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Signed player tokens: base64url("v2|player_id|session_id|expiry") + "." + base64url(HMAC-SHA256). The
/// session is the login the token came from. Each login gives the player a new one (players.session_id), and
/// <see cref="PlayerTokenHandler"/> only accepts a token from their latest, so logging in somewhere else signs
/// out the old place. v1 tokens, from before sessions, are no longer accepted: their players log in again.
/// </summary>
public sealed class PlayerTokens
{
    private const string Version = "v2";
    private const int MinKeyBytes = 32;

    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;

    public PlayerTokens(byte[] key, TimeSpan lifetime, TimeProvider time)
    {
        if (key is null || key.Length < MinKeyBytes)
        {
            throw new ArgumentException($"The token signing key must be at least {MinKeyBytes} bytes.", nameof(key));
        }

        _key = key;
        _lifetime = lifetime;
        _time = time;
    }

    public static PlayerTokens FromOptions(BackendOptions options, TimeProvider time)
    {
        if (string.IsNullOrWhiteSpace(options.TokenSigningKey))
        {
            throw new InvalidOperationException(
                "Backend:TokenSigningKey is not set. Generate one with: openssl rand -base64 32 " +
                "(appsettings.Development.json has a dev-only key for local runs).");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(options.TokenSigningKey);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Backend:TokenSigningKey must be base64.");
        }

        return new PlayerTokens(key, TimeSpan.FromHours(options.TokenLifetimeHours), time);
    }

    /// <param name="sessionId">The login it's for (LoginResult.SessionId).</param>
    public IssuedToken Issue(string playerId, string sessionId)
    {
        var expires = _time.GetUtcNow() + _lifetime;
        var payload = Encoding.UTF8.GetBytes($"{Version}|{playerId}|{sessionId}|{expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}");
        var token = Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(Sign(payload));
        return new IssuedToken(token, DateTimeOffset.FromUnixTimeSeconds(expires.ToUnixTimeSeconds()));
    }

    public bool TryValidate(string? token, out string playerId) => TryValidate(token, out playerId, out _);

    /// <summary>Whether the token is genuine and unexpired. Whether its session is still the player's is the handler's check.</summary>
    public bool TryValidate(string? token, out string playerId, out string sessionId)
    {
        playerId = "";
        sessionId = "";
        if (string.IsNullOrEmpty(token) || token.Length > 512)
        {
            return false;
        }

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot != token.LastIndexOf('.'))
        {
            return false;
        }

        byte[] payload, signature;
        try
        {
            payload = Base64Url.DecodeFromChars(token.AsSpan(0, dot));
            signature = Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(Sign(payload), signature))
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(payload).Split('|');
        if (parts.Length != 4 || parts[0] != Version || !Uuid7.IsValid(parts[1]) || !Uuid7.IsValid(parts[2])
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix))
        {
            return false;
        }

        if (_time.GetUtcNow().ToUnixTimeSeconds() >= expiresUnix)
        {
            return false;
        }

        playerId = parts[1];
        sessionId = parts[2];
        return true;
    }

    private byte[] Sign(byte[] payload) => HMACSHA256.HashData(_key, payload);
}
