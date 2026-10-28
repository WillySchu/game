using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;

using static RaidZero.Raid.Tests.RaidTestKit;

namespace RaidZero.Raid.Tests;

/// <summary>The raid's inventory rules, with a small catalog built in code.</summary>
public class RaidInventoryServiceTests
{
    private readonly RaidInventoryService _raid = new(Catalog);

    private ItemNode Join(string playerId)
    {
        var loadout = Loadout();
        Assert.True(_raid.AddPlayer(playerId, loadout));
        return loadout;
    }

    private ItemNode Crate(params ItemNode[] contents)
    {
        var crate = Node("crate", children: contents);
        _raid.AddContainer(crate);
        return crate;
    }

    private static void AssertOk(RaidInventoryResult result) => Assert.True(result.Success, result.ToString());

    private static void AssertRefused(RaidInventoryResult result, string code)
    {
        Assert.False(result.Success);
        Assert.Equal(code, result.Error.Code);
        Assert.Empty(result.Updates);
    }

    private string RootOfItem(string itemId)
    {
        foreach (var player in new[] { "sam", "alex", "kim" })
        {
            var character = _raid.CharacterOf(player);
            if (character != null && _raid.Snapshot(character)!.Walk().Any(n => n.Id == itemId))
            {
                return character;
            }
        }

        return "elsewhere";
    }

    // ---- Joining ----

    [Fact]
    public void ACatalogWithoutGroundPiles_IsRefused()
    {
        var withoutGround = new ItemCatalog(Catalog.All.Where(d => d.DefId != "ground"));

        Assert.Throws<ArgumentException>(() => new RaidInventoryService(withoutGround));
    }

    [Fact]
    public void Joining_AgainIsAReconnect_AndBadLoadoutsAreRefused()
    {
        var sam = Join("sam");

        Assert.False(_raid.AddPlayer("sam", Loadout()));
        Assert.Equal(sam.Id, _raid.CharacterOf("sam"));
        Assert.Throws<ArgumentException>(() => _raid.AddPlayer("alex", Node("crate")));
        Assert.Throws<ArgumentException>(() => _raid.AddPlayer("kim", sam.Clone()));
        Assert.False(_raid.IsActive("kim"));
    }

    [Fact]
    public void Containers_MustBeAbleToHoldThings()
    {
        Assert.Throws<ArgumentException>(() => _raid.AddContainer(Node("gpu")));
        Assert.Throws<ArgumentException>(() => _raid.AddContainer(Node("character")));
        Assert.Throws<ArgumentException>(() => _raid.AddContainer(Node("ground")));
    }

    // ---- Own gear ----

    [Fact]
    public void RearrangingYourOwnGear_UpdatesOnlyYou()
    {
        var sam = Join("sam");
        Join("alex");
        var mag = sam.Children[0].Children[0];

        var result = _raid.Apply("sam", [InventoryOp.Move(mag.Id, InventoryTarget.AnyGridSpot(Child(sam, "backpack")))]);

        AssertOk(result);
        var update = Assert.Single(result.Updates);
        Assert.Equal(("sam", sam.Id), (update.PlayerId, update.RootId));
        Assert.Contains(update.Root!.Walk(), n => n.Id == mag.Id && n.Slot == SlotNames.Grid);
    }

    [Fact]
    public void Discarding_IsRefused_ItemsOnlyLeaveByDropping()
    {
        var sam = Join("sam");

        AssertRefused(_raid.Apply("sam", [InventoryOp.Discard(Child(sam, "rig"))]), RaidInventoryErrorCodes.NotAllowed);
        Assert.NotNull(_raid.Find(Child(sam, "rig")));
    }

    [Fact]
    public void AnotherLivingPlayersGear_IsOutOfReach()
    {
        var sam = Join("sam");
        var alex = Join("alex");

        AssertRefused(_raid.Apply("sam", [InventoryOp.Move(Child(alex, "rig"), InventoryTarget.AnyGridSpot(Child(sam, "backpack")))]), RaidInventoryErrorCodes.OutOfReach);
        AssertRefused(_raid.Apply("sam", [InventoryOp.Move(Child(sam, "rig"), InventoryTarget.AnyGridSpot(Child(alex, "backpack")))]), RaidInventoryErrorCodes.OutOfReach);
        AssertRefused(_raid.Open("sam", alex.Id), RaidInventoryErrorCodes.NotAllowed);
        AssertRefused(_raid.PickUp("sam", Child(alex, "rig")), RaidInventoryErrorCodes.OutOfReach);
    }

    // ---- Crates ----

    [Fact]
    public void ACrate_CanOnlyBeLootedWhileOpen()
    {
        var sam = Join("sam");
        var gpu = Node("gpu", "grid", 0, 0);
        var crate = Crate(gpu);
        var move = new[] { InventoryOp.Move(gpu.Id, InventoryTarget.AnyGridSpot(Child(sam, "backpack"))) };

        AssertRefused(_raid.Apply("sam", move), RaidInventoryErrorCodes.OutOfReach);

        var opened = _raid.Open("sam", crate.Id);
        AssertOk(opened);
        Assert.Equal(gpu.Id, Assert.Single(opened.Updates).Root!.Children.Single().Id);
        AssertOk(_raid.Apply("sam", move));
        Assert.Equal(sam.Id, RootOfItem(gpu.Id));

        _raid.Close("sam", crate.Id);
        AssertRefused(_raid.Apply("sam", [InventoryOp.Move(gpu.Id, InventoryTarget.AnyGridSpot(crate.Id))]), RaidInventoryErrorCodes.OutOfReach);
    }

    [Fact]
    public void EveryoneLookingIntoACrate_SeesItChange()
    {
        var sam = Join("sam");
        Join("alex");
        Join("kim");
        var gpu = Node("gpu", "grid", 0, 0);
        var crate = Crate(gpu, Node("fuse", "grid", 3, 0));
        _raid.Open("sam", crate.Id);
        _raid.Open("alex", crate.Id);

        var result = _raid.Apply("sam", [InventoryOp.Move(gpu.Id, InventoryTarget.AnyGridSpot(Child(sam, "backpack")))]);

        AssertOk(result);
        var seen = result.Updates.Select(u => (u.PlayerId, u.RootId)).OrderBy(u => u.PlayerId).ThenBy(u => u.RootId == crate.Id).ToList();
        Assert.Equal(new[] { ("alex", crate.Id), ("sam", sam.Id), ("sam", crate.Id) }, seen);
        var alexView = result.Updates.Single(u => u.PlayerId == "alex").Root!;
        Assert.DoesNotContain(alexView.Children, c => c.Id == gpu.Id);
    }

    [Fact]
    public void LootingTheSameItemTwice_OnlyWorksOnce()
    {
        var sam = Join("sam");
        var alex = Join("alex");
        var gpu = Node("gpu", "grid", 0, 0);
        var crate = Crate(gpu);
        _raid.Open("sam", crate.Id);
        _raid.Open("alex", crate.Id);

        AssertOk(_raid.PickUp("sam", gpu.Id));
        var second = _raid.Apply("alex", [InventoryOp.Move(gpu.Id, InventoryTarget.AnyGridSpot(Child(alex, "backpack")))]);

        // It's in Sam's gear now, which Alex can't reach.
        AssertRefused(second, RaidInventoryErrorCodes.OutOfReach);
        Assert.Equal(sam.Id, RootOfItem(gpu.Id));
    }

    // ---- Picking up ----

    [Fact]
    public void PickingUp_FillsAnEmptyMatchingSlotFirst_ThenTheRig_ThenTheBackpack()
    {
        var sam = Join("sam");
        _raid.Apply("sam", [InventoryOp.Move(Child(sam, "primary"), InventoryTarget.AnyGridSpot(Child(sam, "backpack")))]);
        var rifle = Node("rifle", "grid", 0, 0);
        var fuses = new[] { Node("fuse", "grid", 0, 2), Node("fuse", "grid", 1, 2), Node("fuse", "grid", 2, 2) };
        var crate = Crate([rifle, .. fuses]);
        _raid.Open("sam", crate.Id);

        AssertOk(_raid.PickUp("sam", rifle.Id));
        foreach (var fuse in fuses)
        {
            AssertOk(_raid.PickUp("sam", fuse.Id));
        }

        var character = _raid.Snapshot(sam.Id)!;
        Assert.Equal(rifle.Id, Child(character, "primary"));
        var rig = character.Children.Single(c => c.Slot == "rig");
        Assert.Equal(new[] { fuses[0].Id, fuses[1].Id }, rig.Children.Select(c => c.Id));
        Assert.Contains(character.Children.Single(c => c.Slot == "backpack").Children, c => c.Id == fuses[2].Id);
    }

    [Fact]
    public void PickingUp_WithNoRoom_ChangesNothing()
    {
        Join("sam");

        // The rig (2x1) takes one GPU and the backpack (4x4) eight, so the tenth has nowhere to go.
        var spots = new[] { (0, 0), (2, 0), (4, 0), (0, 1), (2, 1), (4, 1), (0, 2), (2, 2), (4, 2), (0, 3) };
        var gpus = spots.Select(p => Node("gpu", "grid", p.Item1, p.Item2)).ToArray();
        var crate = Crate(gpus);
        _raid.Open("sam", crate.Id);
        foreach (var gpu in gpus.Take(9))
        {
            AssertOk(_raid.PickUp("sam", gpu.Id));
        }

        AssertRefused(_raid.PickUp("sam", gpus[9].Id), InventoryErrorCodes.NoSpace);
        Assert.Equal(gpus[9].Id, _raid.Snapshot(crate.Id)!.Children.Single().Id);
    }

    [Fact]
    public void PickingUp_NeedsTheContainerOpen_UnlessItsOnTheGround()
    {
        var sam = Join("sam");
        var gpu = Node("gpu", "grid", 0, 0);
        Crate(gpu);

        AssertRefused(_raid.PickUp("sam", gpu.Id), RaidInventoryErrorCodes.OutOfReach);
        AssertRefused(_raid.PickUp("sam", Child(sam, "rig")), RaidInventoryErrorCodes.NotAllowed);
        AssertRefused(_raid.PickUp("sam", "nope"), InventoryErrorCodes.ItemNotFound);
    }

    // ---- Dropping ----

    [Fact]
    public void ADrop_LandsInANewPileThatAnyoneCanPickUp_AndThePileGoesWhenEmpty()
    {
        var sam = Join("sam");
        var alex = Join("alex");
        Join("kim");
        var rig = Child(sam, "rig");

        var dropped = _raid.Drop("sam", rig);

        AssertOk(dropped);
        var pile = Assert.Single(dropped.CreatedRoots);
        Assert.Equal(RootKind.Ground, _raid.KindOf(pile));
        Assert.Equal(rig, _raid.Snapshot(pile)!.Children.Single().Id);
        Assert.Equal(sam.Id, Assert.Single(dropped.Updates).RootId);

        // Kim is looking at the pile when Alex grabs it.
        _raid.Open("kim", pile);
        _raid.Apply("alex", [InventoryOp.Move(Child(alex, "rig"), InventoryTarget.AnyGridSpot(Child(alex, "backpack")))]);
        var picked = _raid.PickUp("alex", rig);

        AssertOk(picked);
        Assert.Equal(new[] { pile }, picked.RemovedRoots);
        Assert.Null(_raid.KindOf(pile));
        Assert.Equal(rig, Child(_raid.Snapshot(alex.Id)!, "rig"));
        var kimSees = picked.Updates.Single(u => u.PlayerId == "kim");
        Assert.True(kimSees.IsRemoved);
        Assert.Empty(_raid.OpenContainersOf("kim"));
    }

    [Fact]
    public void Dropping_OnlyWorksForYourOwnThings()
    {
        Join("sam");
        var alex = Join("alex");
        var crate = Crate(Node("gpu", "grid", 0, 0));
        _raid.Open("sam", crate.Id);

        AssertRefused(_raid.Drop("sam", Child(alex, "rig")), InventoryErrorCodes.ItemNotFound);
        AssertRefused(_raid.Drop("sam", crate.Children[0].Id), InventoryErrorCodes.ItemNotFound);
        AssertRefused(_raid.Drop("sam", _raid.CharacterOf("sam")!), InventoryErrorCodes.ItemNotFound);
    }

    // ---- Shooting ----

    [Fact]
    public void Consuming_TakesRoundsFromTheMagazine_AndNeverMoreThanThereAre()
    {
        var sam = Join("sam");
        var rounds = sam.Children[0].Children[0].Children[0].Id;

        var shot = _raid.Consume("sam", rounds, 1);

        AssertOk(shot);
        Assert.Equal(29, _raid.Find(rounds)!.Quantity);
        Assert.Equal(sam.Id, Assert.Single(shot.Updates).RootId);
        AssertRefused(_raid.Consume("sam", rounds, 30), InventoryErrorCodes.InvalidQuantity);
        AssertOk(_raid.Consume("sam", rounds, 29));
        Assert.Null(_raid.Find(rounds));
    }

    [Fact]
    public void Consuming_SomeoneElsesItems_IsRefused()
    {
        Join("sam");
        var gpu = Node("gpu", "grid", 0, 0);
        var crate = Crate(gpu);
        _raid.Open("sam", crate.Id);

        AssertRefused(_raid.Consume("sam", gpu.Id, 1), InventoryErrorCodes.ItemNotFound);
    }

    // ---- Death and extraction ----

    [Fact]
    public void ADeadPlayer_BecomesABodyAnyoneCanLoot()
    {
        var sam = Join("sam");
        var alex = Join("alex");
        var crate = Crate(Node("fuse", "grid", 0, 0));
        _raid.Open("alex", crate.Id);
        var alexRifle = Child(alex, "primary");

        _raid.Kill("alex");

        Assert.False(_raid.IsActive("alex"));
        Assert.Empty(_raid.OpenContainersOf("alex"));
        Assert.Equal(RootKind.Body, _raid.KindOf(alex.Id));
        AssertRefused(_raid.Apply("alex", [InventoryOp.Move(Child(alex, "rig"), InventoryTarget.AnyGridSpot(Child(alex, "backpack")))]), RaidInventoryErrorCodes.PlayerNotActive);

        AssertOk(_raid.Open("sam", alex.Id));
        AssertOk(_raid.PickUp("sam", alexRifle));
        Assert.Equal(sam.Id, RootOfItem(alexRifle));
        Assert.Throws<InvalidOperationException>(() => _raid.Kill("alex"));
        Assert.Throws<InvalidOperationException>(() => _raid.AddPlayer("alex", Loadout()));
    }

    [Fact]
    public void Extracting_HandsBackTheirGearWithTheLoot_AndTakesThemOut()
    {
        var sam = Join("sam");
        var gpu = Node("gpu", "grid", 0, 0);
        var crate = Crate(gpu);
        _raid.Open("sam", crate.Id);
        _raid.PickUp("sam", gpu.Id);

        var tree = _raid.Extract("sam");

        Assert.Equal(sam.Id, tree.Id);
        Assert.Contains(tree.Walk(), n => n.Id == gpu.Id);
        Assert.True(InventoryValidator.IsValid(Catalog, tree));
        Assert.Null(_raid.KindOf(sam.Id));
        Assert.Null(_raid.Find(gpu.Id));
        Assert.Null(_raid.CharacterOf("sam"));
        AssertRefused(_raid.Open("sam", crate.Id), RaidInventoryErrorCodes.PlayerNotActive);
        Assert.Throws<InvalidOperationException>(() => _raid.Extract("sam"));
    }

    [Fact]
    public void ARefusedBatch_ChangesNothing()
    {
        var sam = Join("sam");
        var before = _raid.Snapshot(sam.Id)!;

        var result = _raid.Apply("sam",
        [
            InventoryOp.Move(Child(sam, "rig"), InventoryTarget.AnyGridSpot(Child(sam, "backpack"))),
            InventoryOp.Move(Child(sam, "backpack"), InventoryTarget.AnyGridSpot(Child(sam, "rig"))),
        ]);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedOpIndex);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(_raid.Snapshot(sam.Id)));
    }

    [Fact]
    public void TheView_IsYourGearThenWhatYouHaveOpen()
    {
        var sam = Join("sam");
        var crate = Crate();
        _raid.Open("sam", crate.Id);

        Assert.Equal(new[] { sam.Id, crate.Id }, _raid.ViewOf("sam").Select(r => r.Id));
        Assert.Empty(_raid.ViewOf("nobody"));
    }
}
