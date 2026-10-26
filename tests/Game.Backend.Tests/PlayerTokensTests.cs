using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using RaidZero.Backend;
using RaidZero.Backend.Auth;
using RaidZero.Shared.Ids;

namespace Game.Backend.Tests;

public class PlayerTokensTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private readonly FixedTime _time = new(new DateTimeOffset(2026, 10, 26, 20, 0, 0, TimeSpan.Zero));

    private PlayerTokens Tokens(byte[]? key = null) => new(key ?? Key, TimeSpan.FromHours(72), _time);

    [Fact]
    public void IssuedToken_ValidatesToItsPlayer()
    {
        var player = Uuid7.NewId();
        var issued = Tokens().Issue(player);

        Assert.True(Tokens().TryValidate(issued.Token, out var validated));
        Assert.Equal(player, validated);
        Assert.Equal(_time.Now.AddHours(72), issued.ExpiresAt);
    }

    [Fact]
    public void ExpiredToken_IsRejected()
    {
        var issued = Tokens().Issue(Uuid7.NewId());

        _time.Now = _time.Now.AddHours(72);

        Assert.False(Tokens().TryValidate(issued.Token, out _));
    }

    [Fact]
    public void TokenSignedWithAnotherKey_IsRejected()
    {
        var issued = Tokens(RandomNumberGenerator.GetBytes(32)).Issue(Uuid7.NewId());

        Assert.False(Tokens().TryValidate(issued.Token, out _));
    }

    [Fact]
    public void TokenWithAnEditedPayload_IsRejected()
    {
        var issued = Tokens().Issue(Uuid7.NewId());
        var signature = issued.Token[(issued.Token.IndexOf('.') + 1)..];
        var forged = Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"v1|{Uuid7.NewId()}|9999999999")) + "." + signature;

        Assert.False(Tokens().TryValidate(forged, out _));
    }

    [Fact]
    public void TokenWithAnEditedSignature_IsRejected()
    {
        var token = Tokens().Issue(Uuid7.NewId()).Token;
        var flipped = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        Assert.False(Tokens().TryValidate(flipped, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    [InlineData("....")]
    [InlineData("!!!.???")]
    public void Garbage_IsRejected(string? token)
    {
        Assert.False(Tokens().TryValidate(token, out var player));
        Assert.Equal("", player);
    }

    [Fact]
    public void ShortKeys_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => new PlayerTokens(new byte[16], TimeSpan.FromHours(1), _time));
    }

    [Fact]
    public void MissingKeyInSettings_SaysHowToMakeOne()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PlayerTokens.FromOptions(new BackendOptions(), _time));

        Assert.Contains("openssl rand -base64 32", ex.Message);
    }
}
