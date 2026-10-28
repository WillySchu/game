using Game.Backend.Tests;
using Newtonsoft.Json.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi.Tests;

/// <summary>The session's handling of failures, against a fake backend.</summary>
public sealed class PlayerSessionTests : IDisposable
{
    private static readonly ItemCatalog Catalog = CatalogJson.LoadFile(TestPaths.CatalogPath);

    private readonly FakeBackend _backend = new();
    private readonly PlayerSession _session;
    private readonly string _stash = Uuid7.NewId();
    private readonly string _fuse = Uuid7.NewId();

    public PlayerSessionTests()
    {
        _session = new PlayerSession("http://backend.test", Catalog,
            new PlayerSessionOptions { ResultPollInterval = TimeSpan.FromMilliseconds(10) }, _backend);
        _backend.Handler = Normal;
    }

    public void Dispose() => _session.Dispose();

    private BackendResponse Normal(BackendRequest request) => request.Path switch
    {
        "/v1/auth/dev-login" => FakeBackend.Ok("""{"player_id":"p1","display_name":"Sam","created":true,"token":"t1","expires_at":"2030-01-01T00:00:00Z"}"""),
        "/v1/me/profile" => FakeBackend.Ok(Profile().ToString()),
        _ => FakeBackend.Error(404, "not_found"),
    };

    private JObject Profile() => new()
    {
        ["player_id"] = "p1",
        ["display_name"] = "Sam",
        ["profile_version"] = 3,
        ["stash"] = new JObject
        {
            ["id"] = _stash, ["def_id"] = "stash", ["rotated"] = false, ["quantity"] = 1,
            ["children"] = new JArray(new JObject { ["id"] = _fuse, ["def_id"] = "loot_fuse", ["slot"] = "grid", ["x"] = 0, ["y"] = 0, ["rotated"] = false, ["quantity"] = 1, ["children"] = new JArray() }),
        },
        ["character"] = new JObject { ["id"] = Uuid7.NewId(), ["def_id"] = "character", ["rotated"] = false, ["quantity"] = 1, ["children"] = new JArray() },
    };

    private IReadOnlyList<InventoryOp> MoveFuse() => [InventoryOp.Move(_fuse, InventoryTarget.Grid(_stash, 4, 4))];

    [Fact]
    public async Task Edits_SendTheVersionTheyWereMadeAgainst()
    {
        await _session.DevLoginAsync("Sam");
        _backend.Handler = r => r.Path == "/v1/me/inventory/ops" ? FakeBackend.Ok("""{"profile_version":4,"created":[]}""") : Normal(r);

        var result = await _session.ApplyAsync(MoveFuse());

        Assert.True(result.Succeeded, result.ToString());
        var sent = JObject.Parse(_backend.To("/inventory/ops").Single().Body);
        Assert.Equal(3, (int)sent["expected_version"]!);
        Assert.Equal(("move", 4, 4), ((string?)sent["ops"]![0]!["op"], (int)sent["ops"]![0]!["to"]!["x"]!, (int)sent["ops"]![0]!["to"]!["y"]!));
        Assert.Equal("Bearer t1", _backend.To("/inventory/ops").Single().Headers["Authorization"]);
        Assert.Equal((4L, 4, 4), (_session.Profile!.Version, _session.Profile.Stash.Children[0].X!.Value, _session.Profile.Stash.Children[0].Y!.Value));
    }

    [Fact]
    public async Task AnExpiredToken_LogsThePlayerOut()
    {
        await _session.DevLoginAsync("Sam");
        var loggedOut = 0;
        _session.LoggedOut += () => loggedOut++;
        _backend.Handler = _ => new BackendResponse(401, "");

        var refresh = await _session.RefreshAsync();
        var edit = await _session.ApplyAsync(MoveFuse());

        Assert.Equal("logged_out", refresh.Error);
        Assert.Equal("not_logged_in", edit.Error);
        Assert.False(_session.IsLoggedIn);
        Assert.Equal(1, loggedOut);
    }

    [Fact]
    public async Task ALostConnection_IsReportedOnce_AndTheEditIsNotResent()
    {
        await _session.DevLoginAsync("Sam");
        _backend.Handler = r => r.Path == "/v1/me/inventory/ops" ? throw new HttpRequestException("network down") : Normal(r);

        var result = await _session.ApplyAsync(MoveFuse());

        // It may have been applied before the connection dropped, so sending it again could apply it twice.
        Assert.Equal("backend_unreachable", result.Error);
        Assert.Single(_backend.To("/inventory/ops"));
    }

    [Fact]
    public async Task WaitingForAResult_PollsUntilItsFinal()
    {
        await _session.DevLoginAsync("Sam");
        var raidId = Uuid7.NewId();
        int polls = 0;
        _backend.Handler = r => r.Path == "/v1/me/raids/" + raidId
            ? FakeBackend.Ok(new JObject { ["raid_id"] = raidId, ["map_id"] = "greybox_01", ["raid_state"] = "active", ["outcome"] = ++polls < 3 ? "in_raid" : "extracted", ["items_gained"] = new JArray("g1"), ["items_returned"] = new JArray(), ["items_lost"] = new JArray() }.ToString())
            : Normal(r);

        var result = await _session.WaitForRaidResultAsync(raidId, TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded, result.ToString());
        Assert.Equal(("extracted", 3), (result.Value.Outcome, polls));
        Assert.Equal(new[] { "g1" }, result.Value.ItemsGained);
        Assert.Equal(2, _backend.To("/v1/me/profile").Count);
    }

    [Fact]
    public async Task WaitingForAResult_GivesUpAfterTheTimeout()
    {
        await _session.DevLoginAsync("Sam");
        var raidId = Uuid7.NewId();
        _backend.Handler = r => r.Path.StartsWith("/v1/me/raids/", StringComparison.Ordinal)
            ? FakeBackend.Ok(new JObject { ["raid_id"] = raidId, ["outcome"] = "in_raid" }.ToString())
            : Normal(r);

        var result = await _session.WaitForRaidResultAsync(raidId, TimeSpan.FromMilliseconds(100));

        Assert.Equal("timed_out", result.Error);
        Assert.Equal("in_raid", result.Value.Outcome);
    }

    [Fact]
    public async Task NothingNeedingALogin_IsSentWithoutOne()
    {
        var result = await _session.RequestTicketAsync("greybox_01");

        Assert.Equal("not_logged_in", result.Error);
        Assert.Empty(_backend.Requests);
    }
}
