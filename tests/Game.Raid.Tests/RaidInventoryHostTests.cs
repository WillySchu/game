using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;
using static RaidZero.Raid.Tests.RaidTestKit;

namespace RaidZero.Raid.Tests;

/// <summary>The server's message handling, mostly driven through real clients' view trackers over bytes.</summary>
public class RaidInventoryHostTests
{
    private readonly RaidInventoryHost _host = new(new RaidInventoryService(Catalog));
    private readonly Dictionary<string, RaidInventoryView> _clients = new();
    private readonly HashSet<(string Player, string Container)> _outOfReach = new();
    private double _now;

    public RaidInventoryHostTests()
    {
        _host.CanReach = (player, container) => !_outOfReach.Contains((player, container));
    }

    /// <summary>A player joins, with a client that receives whatever the server sends them.</summary>
    private (RaidInventoryView Client, ItemNode Loadout) Join(string playerId)
    {
        var loadout = Loadout();
        _clients[playerId] = new RaidInventoryView();
        Deliver(_host.Join(playerId, loadout));
        return (_clients[playerId], loadout);
    }

    private ItemNode Crate(params ItemNode[] contents)
    {
        var crate = Node("crate", children: contents);
        _host.Service.AddContainer(crate);
        return crate;
    }

    /// <summary>Sends a client's bytes to the server and delivers everything that comes back.</summary>
    private HostOutput Send(string playerId, byte[] data)
    {
        _now += 0.01;
        var output = _host.Receive(playerId, data, _now);
        Deliver(output);
        return output;
    }

    private void Deliver(HostOutput output)
    {
        foreach (var message in output.Messages)
        {
            if (_clients.TryGetValue(message.PlayerId, out var client))
            {
                client.Receive(message.Data);
            }
        }
    }

    private static RequestResult LastResult(HostOutput output, string playerId) =>
        (RequestResult)RaidProtocol.DecodeFromServer(output.Messages.Last(m => m.PlayerId == playerId).Data);

    private static string Bytes(ItemNode node) => Convert.ToBase64String(RaidProtocol.Encode(new ContainerView { Kind = RootKind.Container, Root = node }));

    [Fact]
    public void Joining_SendsThePlayerTheirGear()
    {
        var (client, loadout) = Join("sam");

        Assert.Equal(loadout.Id, client.Character.Root.Id);
        Assert.Equal(Bytes(_host.Service.Snapshot(loadout.Id)!), Bytes(client.Character.Root));
    }

    [Fact]
    public void ARequest_SendsTheNewViewsFirst_ThenTheAnswer()
    {
        var (sam, loadout) = Join("sam");
        var mag = loadout.Children[0].Children[0].Id;
        var answered = new List<RequestResult>();
        sam.RequestCompleted += answered.Add;

        var output = Send("sam", sam.Request([InventoryOp.Move(mag, InventoryTarget.AnyGridSpot(Child(loadout, "backpack")))]));

        Assert.Equal(new[] { typeof(ContainerView), typeof(RequestResult) }, output.Messages.Select(m => RaidProtocol.DecodeFromServer(m.Data).GetType()));
        Assert.True(Assert.Single(answered).Success);
        Assert.False(sam.IsPending(mag));
        Assert.Equal(Bytes(_host.Service.Snapshot(loadout.Id)!), Bytes(sam.Character.Root));
    }

    [Fact]
    public void ARefusedRequest_OnlyGetsAnAnswer()
    {
        var (sam, loadout) = Join("sam");

        var output = Send("sam", sam.Request([InventoryOp.Discard(Child(loadout, "rig"))]));

        var result = (RequestResult)RaidProtocol.DecodeFromServer(Assert.Single(output.Messages).Data);
        Assert.Equal((false, RaidInventoryErrorCodes.NotAllowed, 0), (result.Success, result.Error, result.FailedOpIndex!.Value));
    }

    [Fact]
    public void LootingACrate_EndToEnd_EveryoneLookingSeesIt()
    {
        var (sam, samGear) = Join("sam");
        var (alex, _) = Join("alex");
        var gpu = Node("gpu", "grid", 0, 0);
        var crate = Crate(gpu);
        Send("sam", sam.Open(crate.Id));
        Send("alex", alex.Open(crate.Id));
        Assert.Equal(gpu.Id, alex.Containers[crate.Id].Root.Children.Single().Id);

        var output = Send("sam", sam.Request([InventoryOp.Move(gpu.Id, InventoryTarget.AnyGridSpot(Child(samGear, "backpack")))]));

        Assert.Empty(alex.Containers[crate.Id].Root.Children);
        Assert.Empty(sam.Containers[crate.Id].Root.Children);
        Assert.Contains(sam.Character.Root.Walk(), n => n.Id == gpu.Id);

        // Both players got the crate's view as the very same bytes, encoded once.
        var crateViews = output.Messages.Where(m => RaidProtocol.DecodeFromServer(m.Data) is ContainerView v && v.Root.Id == crate.Id).ToList();
        Assert.Equal(2, crateViews.Count);
        Assert.Same(crateViews[0].Data, crateViews[1].Data);
    }

    [Fact]
    public void OpeningSomethingTooFarAway_IsRefused()
    {
        var (sam, _) = Join("sam");
        var crate = Crate();
        _outOfReach.Add(("sam", crate.Id));

        var output = Send("sam", sam.Open(crate.Id));

        Assert.Equal(RaidInventoryErrorCodes.OutOfReach, LastResult(output, "sam").Error);
        Assert.Empty(_host.Service.OpenContainersOf("sam"));
    }

    [Fact]
    public void WalkingAway_ClosesTheContainer()
    {
        var (sam, _) = Join("sam");
        var crate = Crate();
        Send("sam", sam.Open(crate.Id));
        Assert.True(sam.Containers.ContainsKey(crate.Id));

        Deliver(_host.Tick(_now));
        Assert.True(sam.Containers.ContainsKey(crate.Id));

        _outOfReach.Add(("sam", crate.Id));
        Deliver(_host.Tick(_now));

        Assert.False(sam.Containers.ContainsKey(crate.Id));
        Assert.Empty(_host.Service.OpenContainersOf("sam"));
    }

    [Fact]
    public void ADrop_SpawnsAPile_ThatsPickedUpWithinReach_AndThenDespawns()
    {
        var (sam, samGear) = Join("sam");
        var (alex, _) = Join("alex");
        _host.Service.Apply("alex", [InventoryOp.Move(Child(_host.Service.Snapshot(_host.Service.CharacterOf("alex"))!, "rig"),
            InventoryTarget.AnyGridSpot(Child(_host.Service.Snapshot(_host.Service.CharacterOf("alex"))!, "backpack")))]);
        var rig = Child(samGear, "rig");

        var dropped = Send("sam", sam.Drop(rig));
        var pile = Assert.Single(dropped.CreatedRoots);
        Assert.True(LastResult(dropped, "sam").Success);

        _outOfReach.Add(("alex", pile));
        Assert.Equal(RaidInventoryErrorCodes.OutOfReach, LastResult(Send("alex", alex.PickUp(rig)), "alex").Error);

        _outOfReach.Clear();
        var picked = Send("alex", alex.PickUp(rig));

        Assert.True(LastResult(picked, "alex").Success);
        Assert.Equal(new[] { pile }, picked.RemovedRoots);
        Assert.Equal(rig, Child(alex.Character.Root, "rig"));
    }

    [Fact]
    public void AFlood_IsRateLimited_ThenAllowedAgain()
    {
        var host = new RaidInventoryHost(new RaidInventoryService(Catalog), new RaidInventoryHostOptions { RequestBurst = 3, RequestsPerSecond = 2 });
        host.Join("sam", Loadout());
        var client = new RaidInventoryView();
        var crate = Node("crate");
        host.Service.AddContainer(crate);

        var errors = Enumerable.Range(0, 4).Select(_ => LastResult(host.Receive("sam", client.Open(crate.Id), 10.0), "sam").Error).ToList();
        var later = LastResult(host.Receive("sam", client.Open(crate.Id), 10.5), "sam");

        Assert.Equal(new string?[] { null, null, null, RaidInventoryErrorCodes.RateLimited }, errors);
        Assert.True(later.Success);
    }

    [Fact]
    public void Garbage_IsReportedAndIgnored()
    {
        Join("sam");
        var violations = new List<(string, string)>();
        _host.ProtocolViolation += (player, reason) => violations.Add((player, reason));

        var garbage = Send("sam", [1, 2, 3]);
        var serverMessage = Send("sam", RaidProtocol.Encode(new ContainerClosed { ContainerId = Uuid7.NewId() }));

        Assert.Empty(garbage.Messages);
        Assert.Empty(serverMessage.Messages);
        Assert.Equal(new[] { "sam", "sam" }, violations.Select(v => v.Item1));
    }

    [Fact]
    public void TheDeadAndTheExtracted_AreTurnedAway()
    {
        var (sam, _) = Join("sam");
        var (alex, alexGear) = Join("alex");
        _host.Kill("sam");
        var extracted = _host.Extract("alex");

        Assert.Equal(alexGear.Id, extracted.Id);
        Assert.Equal(RaidInventoryErrorCodes.PlayerNotActive, LastResult(Send("sam", sam.Open(Uuid7.NewId())), "sam").Error);
        Assert.Equal(RaidInventoryErrorCodes.PlayerNotActive, LastResult(Send("alex", alex.Open(Uuid7.NewId())), "alex").Error);
    }

    [Fact]
    public void ShootingDoesntResendGearEveryShot_TheViewFollowsWithinTheDelay()
    {
        var (sam, loadout) = Join("sam");
        var rounds = loadout.Children[0].Children[0].Children[0].Id;

        for (int shot = 0; shot < 5; shot++)
        {
            Assert.True(_host.Consume("sam", rounds, 1, 1.0 + (shot * 0.1)));
        }

        Assert.Empty(_host.Tick(1.5).Messages);
        Assert.Equal(30, sam.Character.Root.Walk().Single(n => n.Id == rounds).Quantity);

        var later = _host.Tick(2.0);
        Deliver(later);

        Assert.Single(later.Messages);
        Assert.Equal(25, sam.Character.Root.Walk().Single(n => n.Id == rounds).Quantity);
        Assert.Empty(_host.Tick(5.0).Messages);
        Assert.False(_host.Consume("sam", rounds, 26, 5.0));
    }

    [Fact]
    public void ARequestAfterShooting_CarriesTheLatestGear_SoNothingWaits()
    {
        var (sam, loadout) = Join("sam");
        var rounds = loadout.Children[0].Children[0].Children[0].Id;
        _host.Consume("sam", rounds, 3, 1.0);

        Send("sam", sam.Request([InventoryOp.Move(Child(loadout, "rig"), InventoryTarget.AnyGridSpot(Child(loadout, "backpack")))]));

        Assert.Equal(27, sam.Character.Root.Walk().Single(n => n.Id == rounds).Quantity);
        Assert.Empty(_host.Tick(10.0).Messages);
    }

    [Fact]
    public void Reconnecting_ResendsGearAndOpenContainers()
    {
        var (sam, loadout) = Join("sam");
        var crate = Crate();
        Send("sam", sam.Open(crate.Id));

        var output = _host.Join("sam", Loadout());

        var roots = output.Messages.Select(m => ((ContainerView)RaidProtocol.DecodeFromServer(m.Data)).Root.Id).ToList();
        Assert.Equal(new[] { loadout.Id, crate.Id }, roots);
    }
}
