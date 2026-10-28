using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;
using static Game.Shared.Tests.TestCatalog;

namespace Game.Shared.Tests;

public class RaidInventoryViewTests
{
    private readonly RaidInventoryView _view = new();

    private static byte[] FromServer(RaidMessage message) => RaidProtocol.Encode(message);

    [Fact]
    public void Requests_AreNumbered_AndTheirItemsStayPendingUntilAnswered()
    {
        var gpu = Uuid7.NewId();
        var stack = Uuid7.NewId();
        var answered = new List<RequestResult>();
        _view.RequestCompleted += answered.Add;

        var first = (InventoryRequest)RaidProtocol.DecodeFromClient(_view.Request([InventoryOp.Merge(gpu, stack)]));
        var second = (PickUpRequest)RaidProtocol.DecodeFromClient(_view.PickUp(Uuid7.NewId()));

        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.True(_view.IsPending(gpu));
        Assert.True(_view.IsPending(stack));
        Assert.Equal(2, _view.PendingCount);

        _view.Receive(FromServer(new RequestResult { RequestId = first.RequestId, Success = false, Error = "stack_full" }));

        Assert.False(_view.IsPending(gpu));
        Assert.Equal("stack_full", Assert.Single(answered).Error);
        Assert.Equal(1, _view.PendingCount);
    }

    [Fact]
    public void Views_ReplaceWhatWasShown_AndClosingRemovesThem()
    {
        var changed = new List<string>();
        var closed = new List<string>();
        _view.ContainerChanged += v => changed.Add(v.Root.Id);
        _view.ContainerClosed += closed.Add;
        var character = ValidLoadout();
        var crate = Node("stash");

        _view.Receive(FromServer(new ContainerView { Kind = RootKind.Character, Root = character }));
        _view.Receive(FromServer(new ContainerView { Kind = RootKind.Container, Root = crate }));
        var emptied = character.Clone();
        emptied.Children.Clear();
        _view.Receive(FromServer(new ContainerView { Kind = RootKind.Character, Root = emptied }));

        Assert.Equal(new[] { character.Id, crate.Id, character.Id }, changed);
        Assert.Empty(_view.Character.Root.Children);
        Assert.Equal(2, _view.Containers.Count);

        _view.Receive(FromServer(new ContainerClosed { ContainerId = crate.Id }));
        var closeBytes = _view.Close(character.Id);

        Assert.Equal(new[] { crate.Id, character.Id }, closed);
        Assert.Empty(_view.Containers);
        Assert.IsType<CloseRequest>(RaidProtocol.DecodeFromClient(closeBytes));
    }

    [Fact]
    public void ARequestFromTheServer_IsRefused()
    {
        Assert.Throws<ProtocolException>(() => _view.Receive(RaidProtocol.Encode(new PickUpRequest { RequestId = 1, ItemId = Uuid7.NewId() })));
    }
}
