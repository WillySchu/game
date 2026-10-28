using RaidZero.Shared.Inventory;
using RaidZero.Shared.InventoryUi;
using RaidZero.Shared.Raid;
using static Game.Shared.Tests.TestCatalog;

namespace Game.Shared.Tests;

/// <summary>The inventory screen's source in a raid, fed real protocol messages as the server would send them.</summary>
public sealed class RaidInventorySourceTests : IDisposable
{
    private readonly RaidInventoryView _view = new();
    private readonly List<RaidMessage> _sent = [];
    private readonly RaidInventorySource _source;
    private readonly ItemNode _character = Node("character", children: [Node("backpack", "backpack")]);
    private readonly ItemNode _crate = Node("stash", children: [Node("gpu", "grid", 0, 0)]);
    private readonly ItemNode _body = Node("character", children: [Node("fuse", "backpack")]);
    private int _changes;
    private readonly List<string> _refusals = [];

    public RaidInventorySourceTests()
    {
        _source = new RaidInventorySource(_view, bytes => _sent.Add(RaidProtocol.DecodeFromClient(bytes)));
        _source.Changed += () => _changes++;
        _source.Refused += _refusals.Add;
    }

    public void Dispose() => _source.Dispose();

    private void FromServer(RaidMessage message) => _view.Receive(RaidProtocol.Encode(message));

    private string[] RootIds() => _source.Roots.Select(r => r.Root.Id).ToArray();

    [Fact]
    public void TheCharacterComesFirst_ThenTheLastContainerOpened()
    {
        FromServer(new ContainerView { Kind = RootKind.Container, Root = _crate });
        FromServer(new ContainerView { Kind = RootKind.Character, Root = _character });
        FromServer(new ContainerView { Kind = RootKind.Body, Root = _body });

        Assert.Equal([_character.Id, _body.Id, _crate.Id], RootIds());
        Assert.Equal([InventoryRootKind.Character, InventoryRootKind.Body, InventoryRootKind.Container], _source.Roots.Select(r => r.Kind));
        Assert.Equal(3, _changes);

        // An update to something already open keeps its place.
        FromServer(new ContainerView { Kind = RootKind.Container, Root = _crate.Clone() });
        Assert.Equal([_character.Id, _body.Id, _crate.Id], RootIds());
    }

    [Fact]
    public void ClosingAContainer_RemovesIt_WhoeverClosesIt()
    {
        FromServer(new ContainerView { Kind = RootKind.Character, Root = _character });
        FromServer(new ContainerView { Kind = RootKind.Container, Root = _crate });
        FromServer(new ContainerView { Kind = RootKind.Body, Root = _body });

        _source.Close(_body.Id);                                     // the player closes it
        FromServer(new ContainerClosed { ContainerId = _crate.Id }); // the server closes it (walked away)

        Assert.Equal([_character.Id], RootIds());
        Assert.Equal(_body.Id, Assert.IsType<CloseRequest>(Assert.Single(_sent)).ContainerId);
    }

    [Fact]
    public void SentItemsArePending_UntilTheServerAnswers_AndRefusalsAreReported()
    {
        FromServer(new ContainerView { Kind = RootKind.Character, Root = _character });
        var gpu = _crate.Children[0];
        var changesBefore = _changes;

        _source.Send([InventoryOp.Move(gpu.Id, InventoryTarget.AnyGridSpot(_character.Children[0].Id))]);

        var request = Assert.IsType<InventoryRequest>(Assert.Single(_sent));
        Assert.True(_source.IsPending(gpu.Id));
        Assert.Equal(changesBefore + 1, _changes);

        FromServer(new RequestResult { RequestId = request.RequestId, Success = false, Error = "out_of_reach" });

        Assert.False(_source.IsPending(gpu.Id));
        Assert.Equal("out_of_reach", Assert.Single(_refusals));
    }

    [Fact]
    public void Dropping_SendsADropRequest()
    {
        _source.Drop(_crate.Children[0].Id);

        Assert.Equal(_crate.Children[0].Id, Assert.IsType<DropRequest>(Assert.Single(_sent)).ItemId);
        Assert.True(_source.CanDrop);
    }

    [Fact]
    public void LeavingTheRaid_EmptiesTheScreen()
    {
        FromServer(new ContainerView { Kind = RootKind.Character, Root = _character });
        FromServer(new ContainerView { Kind = RootKind.Container, Root = _crate });
        var changesBefore = _changes;

        _view.Clear();

        Assert.Empty(_source.Roots);
        Assert.Equal(changesBefore + 1, _changes);

        // A new raid starts the order over.
        FromServer(new ContainerView { Kind = RootKind.Character, Root = _character });
        Assert.Equal([_character.Id], RootIds());
    }

    [Fact]
    public void ASourceMadeMidRaid_ShowsWhatsAlreadyOpen()
    {
        FromServer(new ContainerView { Kind = RootKind.Character, Root = _character });
        FromServer(new ContainerView { Kind = RootKind.Container, Root = _crate });

        using var late = new RaidInventorySource(_view, _ => { });

        Assert.Equal([_character.Id, _crate.Id], late.Roots.Select(r => r.Root.Id));
    }

    [Fact]
    public void OnceDisposed_ItStopsListening()
    {
        _source.Dispose();

        FromServer(new ContainerView { Kind = RootKind.Character, Root = _character });

        Assert.Equal(0, _changes);
    }
}
