using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace RaidZero.Backend.Auth;

/// <summary>The configured game servers, checked once at startup.</summary>
public sealed class GameServerRegistry
{
    private const int MinTokenLength = 32;

    private readonly List<(GameServerOptions Server, byte[] TokenHash)> _servers;

    public GameServerRegistry(IEnumerable<GameServerOptions> servers)
    {
        var list = servers.ToList();
        var problems = new List<string>();
        foreach (var server in list)
        {
            if (string.IsNullOrWhiteSpace(server.Id) || !server.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                problems.Add($"server id '{server.Id}' must be letters, digits, hyphens or underscores");
            }

            if (server.Token.Length < MinTokenLength)
            {
                problems.Add($"server '{server.Id}' needs a token of at least {MinTokenLength} characters (openssl rand -base64 32)");
            }
        }

        foreach (var duplicate in list.GroupBy(s => s.Id).Where(g => g.Count() > 1))
        {
            problems.Add($"server id '{duplicate.Key}' is listed more than once");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Backend:Servers is invalid:\n  " + string.Join("\n  ", problems));
        }

        _servers = list.Select(s => (s, Hash(s.Token))).ToList();
    }

    /// <summary>In configuration order; the first one with room gets new raids.</summary>
    public IReadOnlyList<GameServerOptions> All => _servers.Select(s => s.Server).ToList();

    public GameServerOptions? Find(string id) => _servers.Select(s => s.Server).FirstOrDefault(s => s.Id == id);

    /// <summary>Compares hashes in constant time, so response timing reveals nothing about the tokens.</summary>
    public GameServerOptions? MatchToken(string token)
    {
        var hash = Hash(token);
        GameServerOptions? match = null;
        foreach (var (server, tokenHash) in _servers)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, tokenHash))
            {
                match = server;
            }
        }

        return match;
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

/// <summary>Authenticates game servers: "Authorization: Bearer &lt;server token&gt;".</summary>
public sealed class ServerTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ServerToken";

    private readonly GameServerRegistry _servers;

    public ServerTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, GameServerRegistry servers)
        : base(options, logger, encoder)
    {
        _servers = servers;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        const string prefix = "Bearer ";
        if (header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var server = _servers.MatchToken(header[prefix.Length..].Trim());
        if (server is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown game server token."));
        }

        var identity = new ClaimsIdentity([new Claim(ServerClaims.ServerIdType, server.Id)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public static class ServerClaims
{
    public const string ServerIdType = "server_id";

    public static string GetServerId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ServerIdType) ?? throw new InvalidOperationException("No authenticated game server on this request.");
}

/// <summary>Authorization policies. Each names its scheme, so a player token never works on a server endpoint, or the reverse.</summary>
public static class Policies
{
    public const string Player = "player";
    public const string GameServer = "game_server";
}
