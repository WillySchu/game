using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace RaidZero.Backend.Auth;

/// <summary>Authenticates "Authorization: Bearer &lt;admin token&gt;" for /v1/admin.</summary>
public sealed class AdminTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "AdminToken";
    public const string ClaimType = "admin";
    public const int MinTokenLength = 32;

    private readonly byte[]? _expectedHash;

    public AdminTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IOptions<BackendOptions> backend)
        : base(options, logger, encoder)
    {
        var token = backend.Value.AdminToken;
        _expectedHash = token.Length >= MinTokenLength ? SHA256.HashData(Encoding.UTF8.GetBytes(token)) : null;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        const string prefix = "Bearer ";
        if (_expectedHash is null || header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var sent = SHA256.HashData(Encoding.UTF8.GetBytes(header[prefix.Length..].Trim()));
        if (!CryptographicOperations.FixedTimeEquals(sent, _expectedHash))
        {
            return Task.FromResult(AuthenticateResult.Fail("Wrong admin token."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimType, "true")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
