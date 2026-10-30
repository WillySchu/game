using Game.Backend.Tests;
using Newtonsoft.Json.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.InventoryUi;

namespace RaidZero.BackendApi.Tests;

/// <summary>The inventory screen's Hideout source: the session's profile as roots, and edits over HTTP.</summary>
public sealed class HideoutInventorySourceTests : IDisposable
{
    private static readonly ItemCatalog Catalog = CatalogJson.LoadFile(TestPaths.CatalogPath);

    private readonly FakeBackend _backend = new();
    private readonly PlayerSession _session;
    private readonly HideoutInventorySource _source;
    private readonly InventoryModel _model;
    private readonly string _stash = Uuid7.NewId();
    private readonly string _character = Uuid7.NewId();
    private readonly string _fuse = Uuid7.NewId();
    private readonly List<string> _refusals = [];
    private Func<BackendRequest, BackendResponse> _ops = _ => FakeBackend.Ok("""{"profile_version":4,"created":[]}""");

    public HideoutInventorySourceTests()
    {
        _session = new PlayerSession("http://backend.test", Catalog, new PlayerSessionOptions(), _backend);
        _backend.Handler = Answer;
        _source = new HideoutInventorySource(_session);
        _model = new InventoryModel(Catalog, _source);
        _model.Refused += _refusals.Add;
    }

    public void Dispose()
    {
        _model.Dispose();
        _source.Dispose();
        _session.Dispose();
    }

    private BackendResponse Answer(BackendRequest request) => request.Path switch
    {
        "/v1/auth/dev-login" => FakeBackend.Ok("""{"player_id":"p1","display_name":"Sam","created":true,"token":"t1","expires_at":"2030-01-01T00:00:00Z"}"""),
        "/v1/me/profile" => FakeBackend.Ok(Profile().ToString()),
        "/v1/me/inventory/ops" => _ops(request),
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
        ["character"] = new JObject { ["id"] = _character, ["def_id"] = "character", ["rotated"] = false, ["quantity"] = 1, ["children"] = new JArray() },
    };

    private DropPlan MoveFuse(int x, int y) => _model.Plan(_fuse, false, DropTarget.GridCell(_stash, "grid", x, y));

    [Fact]
    public async Task TheRoots_AreTheCharacterThenTheStash_OnceLoggedIn()
    {
        var changes = 0;
        _model.Changed += () => changes++;
        Assert.Empty(_source.Roots);

        await _session.DevLoginAsync("Sam");

        Assert.Equal([(InventoryRootKind.Character, _character), (InventoryRootKind.Stash, _stash)], _source.Roots.Select(r => (r.Kind, r.Root.Id)));
        Assert.Equal(_character, _model.Character!.Root.Id);
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task AnEdit_GoesToTheBackend_AndTheScreenSeesTheResult()
    {
        await _session.DevLoginAsync("Sam");

        var plan = MoveFuse(5, 5);
        Assert.True(_model.Execute(_fuse, plan), plan.ToString());
        await Wait.ForAsync(_source.Settled, "the edit");

        var sent = JObject.Parse(_backend.To("/inventory/ops").Single().Body);
        Assert.Equal((3, "move", 5), ((int)sent["expected_version"]!, (string?)sent["ops"]![0]!["op"], (int)sent["ops"]![0]!["to"]!["x"]!));
        Assert.True(_model.Tree.TryGet(_fuse, out var fuse));
        Assert.Equal((5, 5), (fuse.X!.Value, fuse.Y!.Value));
        Assert.False(_model.IsPending(_fuse));
        Assert.Empty(_refusals);
    }

    [Fact]
    public async Task ItemsArePending_UntilTheBackendAnswers()
    {
        await _session.DevLoginAsync("Sam");
        using var release = new ManualResetEventSlim();
        var ops = _ops;
        _ops = r => { release.Wait(TimeSpan.FromSeconds(10)); return ops(r); };

        _model.Execute(_fuse, MoveFuse(5, 5));

        Assert.True(_model.IsPending(_fuse));
        Assert.Equal(InventoryUiErrors.Pending, MoveFuse(6, 6).Error); // the screen won't stack a second move on it
        Assert.False(_source.Settled.IsCompleted);
        release.Set();
        await Wait.ForAsync(_source.Settled, "the edit");
        Assert.False(_model.IsPending(_fuse));
    }

    [Fact]
    public async Task ARefusal_ComesBackAsASentence_AndShowsTheBackendsStash()
    {
        await _session.DevLoginAsync("Sam");
        _ops = _ => FakeBackend.Error(409, "in_raid", new { raid_id = Uuid7.NewId() });

        _model.Execute(_fuse, MoveFuse(5, 5));
        await Wait.ForAsync(_source.Settled, "the edit");

        Assert.Equal("Your stash is locked while you're in a raid.", Assert.Single(_refusals));
        Assert.True(_model.Tree.TryGet(_fuse, out var fuse));
        Assert.Equal((0, 0), (fuse.X!.Value, fuse.Y!.Value)); // where the backend says it is
        Assert.False(_model.IsPending(_fuse));
    }

    [Fact]
    public async Task ABackendThatCantBeReached_IsARefusalToo()
    {
        await _session.DevLoginAsync("Sam");
        _ops = _ => throw new HttpRequestException("no route to host");

        _model.Execute(_fuse, MoveFuse(5, 5));
        await Wait.ForAsync(_source.Settled, "the edit");

        Assert.Equal("Can't reach the server. Try again in a moment.", Assert.Single(_refusals));
        Assert.False(_model.IsPending(_fuse));
    }

    [Fact]
    public async Task NothingCanBeDropped_InTheHideout()
    {
        await _session.DevLoginAsync("Sam");

        var plan = _model.Plan(_fuse, false, DropTarget.Outside);

        Assert.Equal(InventoryUiErrors.CantDrop, plan.Error);
        Assert.False(_model.Execute(_fuse, plan));
        Assert.Empty(_backend.To("/inventory/ops"));
    }

    [Fact]
    public async Task LoggingOut_EmptiesTheScreen()
    {
        await _session.DevLoginAsync("Sam");
        _backend.Handler = _ => new BackendResponse(401, "");

        await _session.RefreshAsync();

        Assert.Empty(_source.Roots);
        Assert.Null(_model.Character);
    }
}
