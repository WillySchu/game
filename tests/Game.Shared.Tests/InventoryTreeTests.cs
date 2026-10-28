using RaidZero.Shared.Inventory;
using static Game.Shared.Tests.TestCatalog;

namespace Game.Shared.Tests;

public class InventoryTreeTests
{
    private readonly RaidZero.Shared.Catalog.ItemCatalog _catalog = Build();

    // Stash (10x10 grid):
    //   ammo 45 at (0,0), ammo 10 at (1,0), gpu at (2,0), fuse at (4,0), empty magazine at (6,0),
    //   spare rifle at (0,6), backpack at (0,2) holding a fuse at (0,0).
    // Character: rifle in primary, with a magazine holding 20 rounds. Backpack slot empty.
    private readonly ItemNode _ammo = Node("ammo", "grid", 0, 0, quantity: 45);
    private readonly ItemNode _ammoSmall = Node("ammo", "grid", 1, 0, quantity: 10);
    private readonly ItemNode _gpu = Node("gpu", "grid", 2, 0);
    private readonly ItemNode _fuse = Node("fuse", "grid", 4, 0);
    private readonly ItemNode _emptyMag = Node("mag_30", "grid", 6, 0);
    private readonly ItemNode _spareRifle = Node("rifle", "grid", 0, 6, durability: 1.0);
    private readonly ItemNode _packFuse = Node("fuse", "grid", 0, 0);
    private readonly ItemNode _backpack;
    private readonly ItemNode _rounds = Node("ammo", "rounds", quantity: 20);
    private readonly ItemNode _rifle;
    private readonly ItemNode _stash;
    private readonly ItemNode _character;
    private readonly InventoryTree _tree;

    public InventoryTreeTests()
    {
        _backpack = Node("backpack", "grid", 0, 2, children: [_packFuse]);
        _rifle = Node("rifle", "primary", durability: 0.9, children: [Node("mag_30", "magazine", children: [_rounds])]);
        _stash = Node("stash", children: [_ammo, _ammoSmall, _gpu, _fuse, _emptyMag, _spareRifle, _backpack]);
        _character = Node("character", children: [_rifle]);
        _tree = new InventoryTree(_catalog, [_stash, _character]);
    }

    private ItemNode Get(string id)
    {
        Assert.True(_tree.TryGet(id, out var node), $"{id} should exist");
        return node;
    }

    private static void AssertFails(ApplyResult result, string code, int? opIndex = 0)
    {
        Assert.False(result.Success, "expected the batch to be rejected");
        Assert.Equal(code, result.Error.Code);
        Assert.Equal(opIndex, result.FailedOpIndex);
        Assert.True(result.Changes.IsEmpty);
    }

    private static void AssertOk(ApplyResult result) =>
        Assert.True(result.Success, result.Error?.ToString());

    // ---- move ----

    [Fact]
    public void Move_WithinAGrid()
    {
        var result = _tree.Apply(InventoryOp.Move(_gpu.Id, InventoryTarget.Grid(_stash.Id, 5, 5)));

        AssertOk(result);
        var gpu = Get(_gpu.Id);
        Assert.Equal((5, 5), (gpu.X, gpu.Y));
        Assert.Equal(_stash.Id, _tree.ParentOf(_gpu.Id).Id);
        Assert.Equal(new[] { _gpu.Id }, result.Changes.Updated);
    }

    [Fact]
    public void Move_IntoAnotherContainersGrid()
    {
        AssertOk(_tree.Apply(InventoryOp.Move(_fuse.Id, InventoryTarget.Grid(_backpack.Id, 1, 0))));

        Assert.Equal(_backpack.Id, _tree.ParentOf(_fuse.Id).Id);
        Assert.Equal(2, Get(_backpack.Id).Children.Count);
    }

    [Fact]
    public void Move_IntoANamedSlotClearsTheGridPosition()
    {
        AssertOk(_tree.Apply(InventoryOp.Move(_backpack.Id, InventoryTarget.NamedSlot(_character.Id, SlotNames.Backpack))));

        var backpack = Get(_backpack.Id);
        Assert.Equal(SlotNames.Backpack, backpack.Slot);
        Assert.Null(backpack.X);
        Assert.Null(backpack.Y);
        Assert.Equal(_character.Id, _tree.ParentOf(_backpack.Id).Id);
    }

    [Fact]
    public void Move_ToWhereTheItemAlreadyIs_ChangesNothing()
    {
        var result = _tree.Apply(
        [
            InventoryOp.Move(_gpu.Id, InventoryTarget.Grid(_stash.Id, 2, 0)),
            InventoryOp.Move(_rifle.Id, InventoryTarget.NamedSlot(_character.Id, SlotNames.Primary)),
        ]);

        AssertOk(result);
        Assert.True(result.Changes.IsEmpty);
    }

    [Fact]
    public void Move_CanOverlapTheCellsTheItemIsLeaving()
    {
        // The 3x3 backpack at (0,2) shifts one column right, onto two of its own columns.
        AssertOk(_tree.Apply(InventoryOp.Move(_backpack.Id, InventoryTarget.Grid(_stash.Id, 1, 2))));
    }

    [Fact]
    public void Move_RotatedToFitAnEdge()
    {
        AssertFails(_tree.Apply(InventoryOp.Move(_gpu.Id, InventoryTarget.Grid(_stash.Id, 9, 0))), InventoryErrorCodes.OutOfBounds);
        AssertOk(_tree.Apply(InventoryOp.Move(_gpu.Id, InventoryTarget.Grid(_stash.Id, 9, 0, rotated: true))));
    }

    [Fact]
    public void Move_OntoAnotherItem_IsRejected()
    {
        AssertFails(_tree.Apply(InventoryOp.Move(_fuse.Id, InventoryTarget.Grid(_stash.Id, 2, 0))), InventoryErrorCodes.Overlap);
    }

    [Fact]
    public void Move_IntoAnOccupiedSlot_IsRejected()
    {
        AssertFails(_tree.Apply(InventoryOp.Move(_spareRifle.Id, InventoryTarget.NamedSlot(_character.Id, SlotNames.Primary))), InventoryErrorCodes.SlotOccupied);
    }

    [Fact]
    public void Move_IntoASlotThatRejectsIt_IsRejected()
    {
        AssertFails(_tree.Apply(InventoryOp.Move(_fuse.Id, InventoryTarget.NamedSlot(_character.Id, SlotNames.Backpack))), InventoryErrorCodes.SlotMismatch);
        AssertFails(_tree.Apply(InventoryOp.Move(_fuse.Id, InventoryTarget.NamedSlot(_character.Id, "armor"))), InventoryErrorCodes.SlotMismatch);
        AssertFails(_tree.Apply(InventoryOp.Move(_fuse.Id, InventoryTarget.AnyGridSpot(_character.Id))), InventoryErrorCodes.SlotMismatch);
    }

    [Fact]
    public void Move_ToTheFirstFreeSpot()
    {
        AssertOk(_tree.Apply(InventoryOp.Move(_fuse.Id, InventoryTarget.AnyGridSpot(_backpack.Id))));

        Assert.Equal((1, 0), (Get(_fuse.Id).X, Get(_fuse.Id).Y));
    }

    [Fact]
    public void Move_WhenThereIsNoRoom_IsRejectedAndTheWholeBatchIsUndone()
    {
        // The gpu in the middle of the 4x3 backpack leaves no 4x2 space for the rifle, either way round.
        var result = _tree.Apply(
        [
            InventoryOp.Move(_gpu.Id, InventoryTarget.Grid(_backpack.Id, 1, 1)),
            InventoryOp.Move(_spareRifle.Id, InventoryTarget.AnyGridSpot(_backpack.Id)),
        ]);

        AssertFails(result, InventoryErrorCodes.NoSpace, opIndex: 1);
        Assert.Equal(_stash.Id, _tree.ParentOf(_gpu.Id).Id);
    }

    [Fact]
    public void Move_IntoItself_IsRejected()
    {
        AssertFails(_tree.Apply(InventoryOp.Move(_backpack.Id, InventoryTarget.AnyGridSpot(_backpack.Id))), InventoryErrorCodes.Cycle);
    }

    [Fact]
    public void Move_ARoot_IsRejected()
    {
        AssertFails(_tree.Apply(InventoryOp.Move(_stash.Id, InventoryTarget.AnyGridSpot(_stash.Id))), InventoryErrorCodes.RootItem);
    }

    [Fact]
    public void Move_UnknownItemOrTarget_IsRejected()
    {
        AssertFails(_tree.Apply(InventoryOp.Move(Uuid(), InventoryTarget.AnyGridSpot(_stash.Id))), InventoryErrorCodes.ItemNotFound);
        AssertFails(_tree.Apply(InventoryOp.Move(_fuse.Id, InventoryTarget.AnyGridSpot(Uuid()))), InventoryErrorCodes.TargetNotFound);
        AssertFails(_tree.Apply(InventoryOp.Move(_fuse.Id, null!)), InventoryErrorCodes.TargetNotFound);
    }

    [Fact]
    public void Move_WithAPositionInANamedSlot_IsRejected()
    {
        var to = InventoryTarget.NamedSlot(_character.Id, SlotNames.Backpack);
        to.X = 0;

        AssertFails(_tree.Apply(InventoryOp.Move(_backpack.Id, to)), InventoryErrorCodes.PositionMismatch);
    }

    [Fact]
    public void Move_WithOnlyOneCoordinate_IsRejected()
    {
        var to = new InventoryTarget { ParentId = _stash.Id, Slot = SlotNames.Grid, X = 5 };

        AssertFails(_tree.Apply(InventoryOp.Move(_fuse.Id, to)), InventoryErrorCodes.PositionMismatch);
    }

    [Fact]
    public void Move_BetweenRoots_LikeAPickup()
    {
        AssertOk(_tree.Apply(
        [
            InventoryOp.Move(_backpack.Id, InventoryTarget.NamedSlot(_character.Id, SlotNames.Backpack)),
            InventoryOp.Move(_gpu.Id, InventoryTarget.AnyGridSpot(_backpack.Id)),
        ]));

        Assert.Equal(_backpack.Id, _tree.ParentOf(_gpu.Id).Id);
        Assert.Equal(_character.Id, _tree.ParentOf(_backpack.Id).Id);
    }

    // ---- split ----

    [Fact]
    public void Split_CreatesANewStack()
    {
        var result = _tree.Apply(InventoryOp.Split(_ammo.Id, 15, InventoryTarget.Grid(_stash.Id, 5, 0)));

        AssertOk(result);
        var created = Assert.Single(result.Created);
        Assert.Equal(0, created.OpIndex);
        Assert.Equal(15, Get(created.ItemId).Quantity);
        Assert.Equal(30, Get(_ammo.Id).Quantity);
        Assert.Equal(new[] { created.ItemId }, result.Changes.Created);
        Assert.Equal(new[] { _ammo.Id }, result.Changes.Updated);
    }

    [Fact]
    public void Split_UsesTheInjectedIdGenerator()
    {
        var id = Uuid();
        var tree = new InventoryTree(_catalog, [Node("stash", children: [Node("ammo", "grid", 0, 0, quantity: 10)])], () => id);
        var ammoId = tree.Roots[0].Children[0].Id;

        var result = tree.Apply(InventoryOp.Split(ammoId, 4, InventoryTarget.AnyGridSpot(tree.Roots[0].Id)));

        AssertOk(result);
        Assert.Equal(id, result.Created.Single().ItemId);
    }

    [Fact]
    public void Split_IntoAMagazineRespectsItsCapacity()
    {
        var rounds = InventoryTarget.NamedSlot(_emptyMag.Id, SlotNames.Rounds);

        AssertFails(_tree.Apply(InventoryOp.Split(_ammo.Id, 31, rounds)), InventoryErrorCodes.QuantityOutOfRange);
        AssertOk(_tree.Apply(InventoryOp.Split(_ammo.Id, 30, rounds)));
        Assert.Equal(15, Get(_ammo.Id).Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(45)]
    [InlineData(50)]
    public void Split_WithAQuantityThatIsntAStrictPart_IsRejected(int quantity)
    {
        AssertFails(_tree.Apply(InventoryOp.Split(_ammo.Id, quantity, InventoryTarget.AnyGridSpot(_stash.Id))), InventoryErrorCodes.InvalidQuantity);
    }

    [Fact]
    public void Split_ANonStackable_IsRejected()
    {
        AssertFails(_tree.Apply(InventoryOp.Split(_gpu.Id, 1, InventoryTarget.AnyGridSpot(_stash.Id))), InventoryErrorCodes.NotStackable);
    }

    // ---- merge ----

    [Fact]
    public void Merge_EverythingRemovesTheSource()
    {
        var result = _tree.Apply(InventoryOp.Merge(_ammoSmall.Id, _ammo.Id));

        AssertOk(result);
        Assert.Equal(55, Get(_ammo.Id).Quantity);
        Assert.False(_tree.TryGet(_ammoSmall.Id, out _));
        Assert.Equal(RemovalReason.Merged, result.Changes.Removed[_ammoSmall.Id]);
        Assert.Equal(new[] { _ammo.Id }, result.Changes.Updated);
    }

    [Fact]
    public void Merge_IntoAMagazineStopsAtItsCapacity()
    {
        var result = _tree.Apply(InventoryOp.Merge(_ammo.Id, _rounds.Id));

        AssertOk(result);
        Assert.Equal(30, Get(_rounds.Id).Quantity);
        Assert.Equal(35, Get(_ammo.Id).Quantity);
        Assert.Equal(2, result.Changes.Updated.Count);
    }

    [Fact]
    public void Merge_ACappedQuantity()
    {
        AssertOk(_tree.Apply(InventoryOp.Merge(_ammo.Id, _ammoSmall.Id, quantity: 5)));

        Assert.Equal(40, Get(_ammo.Id).Quantity);
        Assert.Equal(15, Get(_ammoSmall.Id).Quantity);
    }

    [Fact]
    public void Merge_IntoAFullStack_IsRejected()
    {
        var result = _tree.Apply(
        [
            InventoryOp.Merge(_ammo.Id, _rounds.Id),
            InventoryOp.Merge(_ammoSmall.Id, _rounds.Id),
        ]);

        AssertFails(result, InventoryErrorCodes.StackFull, opIndex: 1);
        Assert.Equal(20, Get(_rounds.Id).Quantity);
    }

    [Fact]
    public void Merge_DifferentItemsOrIntoItself_IsRejected()
    {
        AssertFails(_tree.Apply(InventoryOp.Merge(_fuse.Id, _gpu.Id)), InventoryErrorCodes.StackMismatch);
        AssertFails(_tree.Apply(InventoryOp.Merge(_ammo.Id, _ammo.Id)), InventoryErrorCodes.StackMismatch);
    }

    [Fact]
    public void Merge_NonStackables_IsRejected()
    {
        AssertFails(_tree.Apply(InventoryOp.Merge(_fuse.Id, _packFuse.Id)), InventoryErrorCodes.NotStackable);
    }

    [Fact]
    public void Merge_MoreThanTheSourceHas_IsRejected()
    {
        AssertFails(_tree.Apply(InventoryOp.Merge(_ammoSmall.Id, _ammo.Id, quantity: 11)), InventoryErrorCodes.InvalidQuantity);
    }

    [Fact]
    public void Merge_IntoAnUnknownStack_IsRejected()
    {
        AssertFails(_tree.Apply(InventoryOp.Merge(_ammo.Id, Uuid())), InventoryErrorCodes.TargetNotFound);
    }

    // ---- discard ----

    [Fact]
    public void Discard_RemovesTheItemAndEverythingInIt()
    {
        var result = _tree.Apply(InventoryOp.Discard(_backpack.Id));

        AssertOk(result);
        Assert.False(_tree.TryGet(_backpack.Id, out _));
        Assert.False(_tree.TryGet(_packFuse.Id, out _));
        Assert.Equal(RemovalReason.Discarded, result.Changes.Removed[_backpack.Id]);
        Assert.Equal(RemovalReason.Discarded, result.Changes.Removed[_packFuse.Id]);
        Assert.Equal(2, result.Changes.Removed.Count);
    }

    [Fact]
    public void Discard_ARoot_IsRejected()
    {
        AssertFails(_tree.Apply(InventoryOp.Discard(_character.Id)), InventoryErrorCodes.RootItem);
    }

    // ---- batches ----

    [Fact]
    public void AFailedBatch_LeavesTheTreeExactlyAsItWas()
    {
        var rootsBefore = _tree.Roots.ToList();

        var result = _tree.Apply(
        [
            InventoryOp.Move(_gpu.Id, InventoryTarget.Grid(_stash.Id, 5, 5)),
            InventoryOp.Discard(Uuid()),
        ]);

        AssertFails(result, InventoryErrorCodes.ItemNotFound, opIndex: 1);
        Assert.Same(rootsBefore[0], _tree.Roots[0]);
        Assert.Equal((2, 0), (Get(_gpu.Id).X, Get(_gpu.Id).Y));
    }

    [Fact]
    public void Changes_AreNetOfTheWholeBatch()
    {
        var piece = Uuid();
        var tree = new InventoryTree(_catalog, [_stash, _character], () => piece);

        // Split a stack off, move it around, then merge it straight back.
        var result = tree.Apply(
        [
            InventoryOp.Split(_ammo.Id, 5, InventoryTarget.Grid(_stash.Id, 8, 8)),
            InventoryOp.Move(piece, InventoryTarget.Grid(_stash.Id, 9, 9)),
            InventoryOp.Merge(piece, _ammo.Id),
            InventoryOp.Move(_fuse.Id, InventoryTarget.Grid(_stash.Id, 5, 5)),
            InventoryOp.Discard(_fuse.Id),
        ]);

        AssertOk(result);
        Assert.Empty(result.Created);
        Assert.Empty(result.Changes.Created);
        Assert.Equal(new[] { _ammo.Id }, result.Changes.Updated);
        Assert.Equal(new[] { _fuse.Id }, result.Changes.Removed.Keys.ToArray());
        Assert.Equal(45, tree.Roots[0].Children.Single(c => c.Id == _ammo.Id).Quantity);
    }

    [Fact]
    public void AnUnknownOp_IsRejected()
    {
        AssertFails(_tree.Apply(new InventoryOp { Op = "teleport", ItemId = _gpu.Id }), InventoryErrorCodes.UnknownOp);
        AssertFails(_tree.Apply(new InventoryOp[] { null! }), InventoryErrorCodes.UnknownOp);
    }

    [Fact]
    public void BadDataAlreadyInARoot_FailsBatchesThatTouchIt_ButNotOthers()
    {
        _gpu.Quantity = 2; // invalid before any op runs: gpus don't stack
        var tree = new InventoryTree(_catalog, [_stash, _character]);

        var touchingStash = tree.Apply(InventoryOp.Move(_fuse.Id, InventoryTarget.Grid(_stash.Id, 5, 5)));
        Assert.False(touchingStash.Success);
        Assert.Null(touchingStash.FailedOpIndex);
        Assert.Equal(InventoryErrorCodes.QuantityOutOfRange, touchingStash.Error.Code);

        // A batch that only touches the character doesn't re-validate the stash, so it still succeeds.
        AssertOk(tree.Apply(InventoryOp.Move(_rifle.Id, InventoryTarget.NamedSlot(_character.Id, SlotNames.Primary))));
    }

    [Fact]
    public void Constructor_RejectsDuplicateIds()
    {
        var twin = Node("fuse", "grid", 9, 9);
        twin.Id = _fuse.Id;
        _stash.Children.Add(twin);

        Assert.Throws<ArgumentException>(() => new InventoryTree(_catalog, [_stash, _character]));
    }

    // ---- roots coming and going, and consumption (what a raid server needs) ----

    [Fact]
    public void RootOf_FindsTheTopContainer()
    {
        Assert.Same(_character, _tree.RootOf(_rounds.Id));
        Assert.Same(_stash, _tree.RootOf(_packFuse.Id));
        Assert.Same(_stash, _tree.RootOf(_stash.Id));
        Assert.Null(_tree.RootOf("nope"));
    }

    [Fact]
    public void AddRoot_MakesItsItemsReachable()
    {
        var crateGpu = Node("gpu", "grid", 0, 0);
        var crate = Node("stash", children: [crateGpu]);

        _tree.AddRoot(crate);

        Assert.Same(crate, _tree.RootOf(crateGpu.Id));
        AssertOk(_tree.Apply(InventoryOp.Move(crateGpu.Id, InventoryTarget.AnyGridSpot(_backpack.Id))));

        // Applying replaces the node objects, so compare IDs from here on.
        Assert.Equal(_stash.Id, _tree.RootOf(crateGpu.Id).Id);
        Assert.Contains(_tree.Roots, r => r.Id == crate.Id);
    }

    [Fact]
    public void AddRoot_RefusesInvalidRootsAndReusedIds()
    {
        var twin = Node("fuse", "grid", 0, 0);
        twin.Id = _fuse.Id;

        Assert.Throws<ArgumentException>(() => _tree.AddRoot(Node("stash", children: [twin])));
        Assert.Throws<ArgumentException>(() => _tree.AddRoot(Node("stash", children: [Node("gpu", "primary")])));
        Assert.Equal(2, _tree.Roots.Count);
        Assert.Same(_stash, _tree.RootOf(_fuse.Id));
    }

    [Fact]
    public void RemoveRoot_TakesEverythingInIt()
    {
        var removed = _tree.RemoveRoot(_character.Id);

        Assert.Same(_character, removed);
        Assert.False(_tree.TryGet(_rounds.Id, out _));
        Assert.Single(_tree.Roots);
        Assert.Null(_tree.RemoveRoot(_character.Id));
        Assert.Null(_tree.RemoveRoot(_gpu.Id));
        AssertFails(_tree.Apply(InventoryOp.Move(_rifle.Id, InventoryTarget.Grid(_stash.Id, 5, 5))), InventoryErrorCodes.ItemNotFound);
    }

    [Fact]
    public void Consume_PartOfAStack_ShrinksIt()
    {
        var result = _tree.Consume(_rounds.Id, 3);

        AssertOk(result);
        Assert.Equal(17, Get(_rounds.Id).Quantity);
        Assert.Equal(new[] { _rounds.Id }, result.Changes.Updated);
    }

    [Fact]
    public void Consume_AllOfIt_RemovesItAndWhatsInside()
    {
        var mag = _rifle.Children[0];

        var result = _tree.Consume(mag.Id, 1);

        AssertOk(result);
        Assert.False(_tree.TryGet(_rounds.Id, out _));
        Assert.Empty(Get(_rifle.Id).Children);
        Assert.Equal(RemovalReason.Consumed, result.Changes.Removed[mag.Id]);
        Assert.Equal(RemovalReason.Consumed, result.Changes.Removed[_rounds.Id]);
        AssertOk(_tree.Apply(InventoryOp.Move(_emptyMag.Id, InventoryTarget.NamedSlot(_rifle.Id, "magazine"))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Consume_RefusesQuantitiesTheStackDoesntHave(int quantity)
    {
        AssertFails(_tree.Consume(_rounds.Id, quantity), InventoryErrorCodes.InvalidQuantity);
        Assert.Equal(20, Get(_rounds.Id).Quantity);
    }

    [Fact]
    public void Consume_RefusesRootsAndUnknownItems()
    {
        AssertFails(_tree.Consume(_character.Id, 1), InventoryErrorCodes.RootItem);
        AssertFails(_tree.Consume("nope", 1), InventoryErrorCodes.ItemNotFound);
    }

    private static string Uuid() => RaidZero.Shared.Ids.Uuid7.NewId();
}
