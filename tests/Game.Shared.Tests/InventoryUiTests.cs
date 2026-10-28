using RaidZero.Shared.Inventory;
using RaidZero.Shared.InventoryUi;
using static Game.Shared.Tests.TestCatalog;

namespace Game.Shared.Tests;

/// <summary>The inventory screen's drop rules, checked against the same rules the server applies.</summary>
public class InventoryUiTests
{
    private readonly RaidZero.Shared.Catalog.ItemCatalog _catalog = Build();

    // Crate (a 10x10 "stash" grid):
    //   ammo 45 at (0,0), ammo 10 at (1,0), gpu at (2,0), fuse at (4,0), empty magazine at (6,0),
    //   full ammo 60 at (7,0), spare rifle (no magazine) at (0,6), spare backpack at (5,6).
    // Character: rifle in primary holding a magazine with 20 rounds; backpack (4x3) holding a fuse at (0,0).
    private readonly ItemNode _ammo = Node("ammo", "grid", 0, 0, quantity: 45);
    private readonly ItemNode _ammoSmall = Node("ammo", "grid", 1, 0, quantity: 10);
    private readonly ItemNode _gpu = Node("gpu", "grid", 2, 0);
    private readonly ItemNode _fuse = Node("fuse", "grid", 4, 0);
    private readonly ItemNode _emptyMag = Node("mag_30", "grid", 6, 0);
    private readonly ItemNode _fullAmmo = Node("ammo", "grid", 7, 0, quantity: 60);
    private readonly ItemNode _spareRifle = Node("rifle", "grid", 0, 6, durability: 1.0);
    private readonly ItemNode _spareBackpack = Node("backpack", "grid", 5, 6);
    private readonly ItemNode _rounds = Node("ammo", "rounds", quantity: 20);
    private readonly ItemNode _packFuse = Node("fuse", "grid", 0, 0);
    private readonly ItemNode _loadedMag;
    private readonly ItemNode _rifle;
    private readonly ItemNode _backpack;
    private readonly ItemNode _crate;
    private readonly ItemNode _character;

    public InventoryUiTests()
    {
        _loadedMag = Node("mag_30", "magazine", children: [_rounds]);
        _rifle = Node("rifle", "primary", durability: 0.9, children: [_loadedMag]);
        _backpack = Node("backpack", "backpack", children: [_packFuse]);
        _crate = Node("stash", children: [_ammo, _ammoSmall, _gpu, _fuse, _emptyMag, _fullAmmo, _spareRifle, _spareBackpack]);
        _character = Node("character", children: [_rifle, _backpack]);
    }

    private InventoryTree Tree() => new(_catalog, [_crate.Clone(), _character.Clone()]);

    private DropPlan Drop(ItemNode item, DropTarget target, bool rotated = false) => DropResolver.Resolve(Tree(), item.Id, rotated, target);

    // ---- the dry run ----

    [Fact]
    public void Check_GivesApplysAnswer_WithoutChangingAnything()
    {
        var tree = Tree();
        var move = new[] { InventoryOp.Move(_gpu.Id, InventoryTarget.Grid(_crate.Id, 8, 8)) };
        var overlap = new[] { InventoryOp.Move(_gpu.Id, InventoryTarget.Grid(_crate.Id, 0, 0)) };

        var checkedMove = tree.Check(move);
        var checkedOverlap = tree.Check(overlap);

        Assert.True(checkedMove.Success);
        Assert.Equal(InventoryErrorCodes.Overlap, checkedOverlap.Error.Code);
        Assert.True(tree.TryGet(_gpu.Id, out var gpu));
        Assert.Equal((2, 0), (gpu.X!.Value, gpu.Y!.Value));

        // Apply agrees, and does change it.
        Assert.True(tree.Apply(move).Success);
        Assert.Equal((8, 8), (Get(tree, _gpu.Id).X!.Value, Get(tree, _gpu.Id).Y!.Value));
    }

    private static ItemNode Get(InventoryTree tree, string id)
    {
        Assert.True(tree.TryGet(id, out var node));
        return node;
    }

    // ---- grid cells ----

    [Fact]
    public void OntoAFreeCell_MovesThere_KeepingTheRotation()
    {
        var plan = Drop(_gpu, DropTarget.GridCell(_crate.Id, "grid", 8, 2), rotated: true);

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal(DropAction.Move, plan.Action);
        var op = Assert.Single(plan.Ops);
        Assert.Equal((InventoryOpKinds.Move, _crate.Id, 8, 2, true), (op.Op, op.To.ParentId, op.To.X!.Value, op.To.Y!.Value, op.To.Rotated));
        Assert.Equal((_crate.Id, 8, 2, 1, 2), (plan.GridId, plan.X, plan.Y, plan.Width, plan.Height)); // the gpu is 2x1, turned
    }

    [Fact]
    public void OntoATakenOrOutOfBoundsCell_IsRefused_WithAReason()
    {
        var taken = Drop(_gpu, DropTarget.GridCell(_crate.Id, "grid", 3, 0));
        var outside = Drop(_gpu, DropTarget.GridCell(_crate.Id, "grid", 9, 0));

        Assert.Equal((false, InventoryErrorCodes.Overlap, "That space is taken."), (taken.IsValid, taken.Error, taken.Message));
        Assert.Equal(InventoryErrorCodes.OutOfBounds, outside.Error);
        Assert.Equal((_crate.Id, 9, 0, 2, 1), (outside.GridId, outside.X, outside.Y, outside.Width, outside.Height));
    }

    [Fact]
    public void BackWhereItStarted_DoesNothing()
    {
        var plan = Drop(_gpu, DropTarget.GridCell(_crate.Id, "grid", 2, 0));

        Assert.Equal((DropAction.None, false, null), (plan.Action, plan.IsValid, plan.Error));
        Assert.Empty(plan.Ops);
    }

    [Fact]
    public void IntoAnotherContainersGrid_MovesItThere()
    {
        var plan = Drop(_gpu, DropTarget.GridCell(_backpack.Id, "grid", 1, 1));

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal(_backpack.Id, Assert.Single(plan.Ops).To.ParentId);
    }

    // ---- other items ----

    [Fact]
    public void OntoTheSameStackableItem_Merges()
    {
        var plan = Drop(_ammoSmall, DropTarget.OnItem(_ammo.Id));

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal((DropAction.Merge, _ammo.Id), (plan.Action, plan.TargetItemId));
        var op = Assert.Single(plan.Ops);
        Assert.Equal((InventoryOpKinds.Merge, _ammoSmall.Id, _ammo.Id), (op.Op, op.ItemId, op.IntoId));
    }

    [Fact]
    public void OntoAFullStack_IsRefused()
    {
        var plan = Drop(_ammoSmall, DropTarget.OnItem(_fullAmmo.Id));

        Assert.Equal((DropAction.Merge, InventoryErrorCodes.StackFull), (plan.Action, plan.Error));
    }

    [Fact]
    public void AMagazineOntoARifleWithNone_LoadsIt()
    {
        var plan = Drop(_emptyMag, DropTarget.OnItem(_spareRifle.Id));

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal(DropAction.Load, plan.Action);
        var op = Assert.Single(plan.Ops);
        Assert.Equal((InventoryOpKinds.Move, _spareRifle.Id, "magazine"), (op.Op, op.To.ParentId, op.To.Slot));
    }

    [Fact]
    public void AMagazineOntoARifleThatHasOne_IsRefused()
    {
        var plan = Drop(_emptyMag, DropTarget.OnItem(_rifle.Id));

        Assert.Equal((DropAction.Load, InventoryErrorCodes.SlotOccupied, _rifle.Id), (plan.Action, plan.Error, plan.TargetItemId));
    }

    [Fact]
    public void MoreRoundsThanAnEmptyMagazineHolds_LoadsWhatFits()
    {
        var plan = Drop(_ammo, DropTarget.OnItem(_emptyMag.Id)); // 45 rounds into a 30-round magazine

        Assert.True(plan.IsValid, plan.ToString());
        var op = Assert.Single(plan.Ops);
        Assert.Equal((InventoryOpKinds.Split, 30, _emptyMag.Id, "rounds"), (op.Op, op.Quantity, op.To.ParentId, op.To.Slot));
    }

    [Fact]
    public void RoundsOntoAPartlyLoadedMagazine_TopItUp()
    {
        var tree = Tree();
        var plan = DropResolver.Resolve(tree, _ammo.Id, false, DropTarget.OnItem(_loadedMag.Id));

        Assert.True(plan.IsValid, plan.ToString());
        var op = Assert.Single(plan.Ops);
        Assert.Equal((InventoryOpKinds.Merge, _rounds.Id), (op.Op, op.IntoId));

        // The merge moves only what fits: 10 of the 45.
        Assert.True(tree.Apply(plan.Ops).Success);
        Assert.Equal((30, 35), (Get(tree, _rounds.Id).Quantity, Get(tree, _ammo.Id).Quantity));
    }

    [Fact]
    public void OntoAContainer_PutsItInside()
    {
        var plan = Drop(_gpu, DropTarget.OnItem(_spareBackpack.Id));

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal((DropAction.PutInside, _spareBackpack.Id), (plan.Action, plan.TargetItemId));
        var op = Assert.Single(plan.Ops);
        Assert.Equal((_spareBackpack.Id, "grid", (int?)null), (op.To.ParentId, op.To.Slot, op.To.X));
    }

    [Fact]
    public void OntoSomethingInsideItself_IsRefused()
    {
        var plan = Drop(_backpack, DropTarget.OnItem(_packFuse.Id));

        Assert.Equal(InventoryErrorCodes.Cycle, plan.Error);
    }

    [Fact]
    public void ItemsThatDontGoTogether_AreRefused()
    {
        var plan = Drop(_gpu, DropTarget.OnItem(_fuse.Id));

        Assert.Equal((InventoryUiErrors.CantCombine, "Those don't go together."), (plan.Error, plan.Message));
    }

    [Fact]
    public void OntoItself_DoesNothing()
    {
        Assert.Equal(DropAction.None, Drop(_gpu, DropTarget.OnItem(_gpu.Id)).Action);
    }

    // ---- slots ----

    [Fact]
    public void IntoAnEmptySlot_MovesThere()
    {
        _character.Children.Remove(_rifle);

        var plan = Drop(_spareRifle, DropTarget.NamedSlot(_character.Id, "primary"));

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal((_character.Id, "primary"), (plan.SlotParentId, plan.SlotName));
        Assert.Equal("primary", Assert.Single(plan.Ops).To.Slot);
    }

    [Fact]
    public void IntoASlotThatDoesntTakeIt_IsRefused()
    {
        _character.Children.Remove(_rifle);

        Assert.Equal(InventoryErrorCodes.SlotMismatch, Drop(_gpu, DropTarget.NamedSlot(_character.Id, "primary")).Error);
    }

    [Fact]
    public void IntoAFilledSlot_MeansOntoWhatsInIt()
    {
        var mag = Drop(_emptyMag, DropTarget.NamedSlot(_character.Id, "primary"));      // onto the rifle there
        var gpu = Drop(_gpu, DropTarget.NamedSlot(_character.Id, "backpack"));         // into the backpack there

        Assert.Equal((DropAction.Load, _rifle.Id), (mag.Action, mag.TargetItemId));
        Assert.Equal((DropAction.PutInside, true), (gpu.Action, gpu.IsValid));
    }

    [Fact]
    public void OutsideTheWindow_Drops()
    {
        var plan = Drop(_gpu, DropTarget.Outside);

        Assert.Equal((DropAction.Drop, true), (plan.Action, plan.IsValid));
        Assert.Empty(plan.Ops);
    }

    // ---- quick move ----

    [Fact]
    public void QuickMove_FromACrate_EquipsIntoAnEmptySlotFirst()
    {
        _character.Children.Remove(_rifle);

        var plan = DropResolver.QuickMove(Tree(), _spareRifle.Id, _character.Id, [_crate.Id]);

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal(("primary", _character.Id), (Assert.Single(plan.Ops).To.Slot, plan.SlotParentId));
    }

    [Fact]
    public void QuickMove_FromACrate_ThenTheBackpack()
    {
        var plan = DropResolver.QuickMove(Tree(), _spareRifle.Id, _character.Id, [_crate.Id]); // primary is taken

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal((_backpack.Id, _backpack.Id), (Assert.Single(plan.Ops).To.ParentId, plan.TargetItemId));
    }

    [Fact]
    public void QuickMove_FromTheCharacter_GoesToTheOpenContainer()
    {
        var plan = DropResolver.QuickMove(Tree(), _packFuse.Id, _character.Id, [_crate.Id]);

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal(_crate.Id, Assert.Single(plan.Ops).To.ParentId);
    }

    [Fact]
    public void QuickMove_WithNowhereToGo_SaysSo()
    {
        var plan = DropResolver.QuickMove(Tree(), _packFuse.Id, _character.Id, []);

        Assert.Equal((false, InventoryUiErrors.NowhereToGo), (plan.IsValid, plan.Error));
    }

    // ---- the model over a source ----

    private sealed class FakeSource : IInventorySource
    {
        public List<InventoryRoot> RootList { get; } = [];

        public HashSet<string> PendingIds { get; } = [];

        public List<IReadOnlyList<InventoryOp>> Sent { get; } = [];

        public List<string> Dropped { get; } = [];

        public event Action? Changed;

        public event Action<string>? Refused;

        public IReadOnlyList<InventoryRoot> Roots => RootList;

        public bool CanDrop { get; set; } = true;

        public bool IsPending(string itemId) => PendingIds.Contains(itemId);

        public void Send(IReadOnlyList<InventoryOp> ops) => Sent.Add(ops);

        public void Drop(string itemId) => Dropped.Add(itemId);

        public void Close(string rootId) => RootList.RemoveAll(r => r.Root.Id == rootId);

        public void RaiseChanged() => Changed?.Invoke();

        public void RaiseRefused(string code) => Refused?.Invoke(code);
    }

    private (InventoryModel Model, FakeSource Source) Model()
    {
        var source = new FakeSource();
        source.RootList.Add(new InventoryRoot(InventoryRootKind.Character, _character.Clone()));
        source.RootList.Add(new InventoryRoot(InventoryRootKind.Container, _crate.Clone()));
        return (new InventoryModel(_catalog, source), source);
    }

    [Fact]
    public void Model_SendsTheOps_OrDropsTheItem()
    {
        var (model, source) = Model();

        var move = model.Plan(_gpu.Id, false, DropTarget.GridCell(_crate.Id, "grid", 8, 8));
        var drop = model.Plan(_fuse.Id, false, DropTarget.Outside);

        Assert.True(model.Execute(_gpu.Id, move));
        Assert.True(model.Execute(_fuse.Id, drop));
        Assert.Same(move.Ops, Assert.Single(source.Sent));
        Assert.Equal(_fuse.Id, Assert.Single(source.Dropped));
    }

    [Fact]
    public void Model_NeverSendsARefusedPlan()
    {
        var (model, source) = Model();

        var plan = model.Plan(_gpu.Id, false, DropTarget.GridCell(_crate.Id, "grid", 0, 0));

        Assert.False(model.Execute(_gpu.Id, plan));
        Assert.Empty(source.Sent);
    }

    [Fact]
    public void Model_RefusesPendingItems_AndDropsWhereThereIsNoGround()
    {
        var (model, source) = Model();
        source.PendingIds.Add(_gpu.Id);
        source.CanDrop = false;

        Assert.Equal(InventoryUiErrors.Pending, model.Plan(_gpu.Id, false, DropTarget.GridCell(_crate.Id, "grid", 8, 8)).Error);
        Assert.Equal(InventoryUiErrors.Pending, model.PlanQuickMove(_gpu.Id).Error);
        Assert.Equal(InventoryUiErrors.CantDrop, model.Plan(_fuse.Id, false, DropTarget.Outside).Error);
    }

    [Fact]
    public void Model_SeesTheSourcesNewState_AfterItChanges()
    {
        var (model, source) = Model();
        Assert.True(model.Plan(_gpu.Id, false, DropTarget.GridCell(_crate.Id, "grid", 8, 8)).IsValid);
        var changes = 0;
        model.Changed += () => changes++;

        // The server put something at (8,8) meanwhile.
        var crate = source.RootList[1].Root;
        crate.Children.Add(Node("fuse", "grid", 8, 8));
        source.RaiseChanged();

        Assert.Equal(1, changes);
        Assert.Equal(InventoryErrorCodes.Overlap, model.Plan(_gpu.Id, false, DropTarget.GridCell(_crate.Id, "grid", 8, 8)).Error);
    }

    [Fact]
    public void Model_TurnsRefusalsIntoSentences()
    {
        var (model, source) = Model();
        var said = new List<string>();
        model.Refused += said.Add;

        source.RaiseRefused("out_of_reach");

        Assert.Equal("That's out of reach.", Assert.Single(said));
    }

    [Fact]
    public void Model_QuickMove_UsesTheCharacterAndWhatsOpen()
    {
        var (model, _) = Model();

        var plan = model.PlanQuickMove(_gpu.Id);

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal(_backpack.Id, Assert.Single(plan.Ops).To.ParentId);
    }

    // ---- part of a stack (shift-drag) ----

    private DropPlan Part(ItemNode item, int quantity, DropTarget target) => DropResolver.ResolvePart(Tree(), item.Id, quantity, false, target);

    [Fact]
    public void Part_IntoACell_SplitsOffThatMany()
    {
        var plan = Part(_ammo, 20, DropTarget.GridCell(_crate.Id, "grid", 8, 8));

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal(DropAction.Move, plan.Action);
        var op = Assert.Single(plan.Ops);
        Assert.Equal((InventoryOpKinds.Split, 20, _crate.Id, 8, 8), (op.Op, op.Quantity, op.To.ParentId, op.To.X!.Value, op.To.Y!.Value));
        Assert.Equal((_crate.Id, 8, 8), (plan.GridId, plan.X, plan.Y)); // the highlight is the same as a whole move's
    }

    [Fact]
    public void Part_OntoTheSameStack_MergesThatMany()
    {
        var tree = Tree();
        var plan = DropResolver.ResolvePart(tree, _ammo.Id, 5, false, DropTarget.OnItem(_ammoSmall.Id));

        Assert.True(plan.IsValid, plan.ToString());
        var op = Assert.Single(plan.Ops);
        Assert.Equal((InventoryOpKinds.Merge, _ammoSmall.Id, 5), (op.Op, op.IntoId, op.Quantity));
        Assert.True(tree.Apply(plan.Ops).Success);
        Assert.Equal((40, 15), (Get(tree, _ammo.Id).Quantity, Get(tree, _ammoSmall.Id).Quantity));
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(40, 30)] // the magazine holds 30, so that's all that goes in
    public void Part_IntoAnEmptyMagazine_LoadsTheSmallerOfTheTwo(int quantity, int loaded)
    {
        var plan = Part(_ammo, quantity, DropTarget.OnItem(_emptyMag.Id));

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal(DropAction.Load, plan.Action);
        var op = Assert.Single(plan.Ops);
        Assert.Equal((InventoryOpKinds.Split, loaded, "rounds"), (op.Op, op.Quantity, op.To.Slot));
    }

    [Fact]
    public void Part_OntoAContainer_PutsThatManyInside()
    {
        var op = Assert.Single(Part(_ammo, 5, DropTarget.OnItem(_spareBackpack.Id)).Ops);

        Assert.Equal((InventoryOpKinds.Split, 5, _spareBackpack.Id), (op.Op, op.Quantity, op.To.ParentId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(99)]
    public void Part_OfNoneOrAll_IsAnOrdinaryDrop(int quantity)
    {
        var op = Assert.Single(Part(_ammo, quantity, DropTarget.GridCell(_crate.Id, "grid", 8, 8)).Ops);

        Assert.Equal(InventoryOpKinds.Move, op.Op);
    }

    [Fact]
    public void Part_OutsideTheWindow_IsRefused()
    {
        var plan = Part(_ammo, 5, DropTarget.Outside);

        Assert.Equal((false, InventoryUiErrors.CantDropPart), (plan.IsValid, plan.Error));
    }

    [Fact]
    public void Part_WhereTheWholeWouldBeRefused_IsRefusedToo()
    {
        Assert.Equal(InventoryErrorCodes.Overlap, Part(_ammo, 5, DropTarget.GridCell(_crate.Id, "grid", 2, 0)).Error);
        Assert.Equal(InventoryErrorCodes.StackFull, Part(_ammo, 5, DropTarget.OnItem(_fullAmmo.Id)).Error);
    }

    [Fact]
    public void Model_PlansPartOfAStack_AndKnowsWhatCanBeSplit()
    {
        var (model, _) = Model();

        var op = Assert.Single(model.Plan(_ammo.Id, false, DropTarget.GridCell(_crate.Id, "grid", 8, 8), quantity: 20).Ops);

        Assert.Equal((InventoryOpKinds.Split, 20), (op.Op, op.Quantity));
        Assert.True(model.CanSplit(_ammo.Id));
        Assert.False(model.CanSplit(_gpu.Id));
    }

    // ---- opening containers inside others ----

    [Fact]
    public void OpenItem_ShowsAContainerInsideAnother()
    {
        var (model, _) = Model();
        var changes = 0;
        model.Changed += () => changes++;

        Assert.True(model.OpenItem(_spareBackpack.Id));

        Assert.Equal([_spareBackpack.Id], model.OpenedItems);
        Assert.True(model.IsOpenedItem(_spareBackpack.Id));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void OpenItem_RefusesWhatHasNoGrid_OrIsAlreadyOnScreen()
    {
        var (model, _) = Model();

        Assert.False(model.OpenItem(_gpu.Id));           // no grid
        Assert.False(model.OpenItem(_backpack.Id));      // equipped: already shown under the gear
        Assert.False(model.OpenItem(_crate.Id));         // a root: already a panel
        Assert.Empty(model.OpenedItems);
    }

    [Fact]
    public void OpenedItems_AreNewestFirst_AndClose()
    {
        var (model, source) = Model();
        var pouch = Node("backpack", "grid", 0, 0);
        source.RootList[1].Root.Children.Add(pouch);
        source.RaiseChanged();

        model.OpenItem(_spareBackpack.Id);
        model.OpenItem(pouch.Id);
        model.OpenItem(_spareBackpack.Id); // again: back to the front

        Assert.Equal([_spareBackpack.Id, pouch.Id], model.OpenedItems);
        model.CloseItem(pouch.Id);
        Assert.Equal([_spareBackpack.Id], model.OpenedItems);
    }

    [Fact]
    public void OpenedItems_DropOut_WhenTheyreGoneOrEquipped()
    {
        var (model, source) = Model();
        model.OpenItem(_spareBackpack.Id);

        // The server says it's now in the character's backpack slot (someone equipped it).
        var crate = source.RootList[1].Root;
        var spare = crate.Children.Single(c => c.Id == _spareBackpack.Id);
        crate.Children.Remove(spare);
        var character = source.RootList[0].Root;
        character.Children.RemoveAll(c => c.Slot == "backpack");
        spare.Slot = "backpack";
        spare.X = null;
        spare.Y = null;
        character.Children.Add(spare);
        source.RaiseChanged();

        Assert.Empty(model.OpenedItems);
    }

    [Fact]
    public void QuickMove_FromTheCharacter_PrefersAnOpenedContainer()
    {
        var (model, _) = Model();
        model.OpenItem(_spareBackpack.Id);

        var plan = model.PlanQuickMove(_packFuse.Id);

        Assert.True(plan.IsValid, plan.ToString());
        Assert.Equal(_spareBackpack.Id, Assert.Single(plan.Ops).To.ParentId);
    }

    // ---- dragging arithmetic ----

    [Theory]
    [InlineData(100f, 100f, 0f, 0f, 2, 2)]   // corner held, pointer exactly on (2,2)
    [InlineData(120f, 120f, 0f, 0f, 2, 2)]   // less than half a cell past: still (2,2)
    [InlineData(130f, 130f, 0f, 0f, 3, 3)]   // more than half: the next cell
    [InlineData(150f, 120f, 75f, 20f, 2, 2)] // held 75px in: the corner is at (75,100)
    [InlineData(10f, 10f, 40f, 40f, -1, -1)] // corner off the grid's top-left: reported, refused later
    public void AnchorCell_SnapsTheItemsCornerToTheNearestCell(float px, float py, float gx, float gy, int x, int y)
    {
        DragGeometry.AnchorCell(px, py, gx, gy, 50f, out var ax, out var ay);

        Assert.Equal((x, y), (ax, ay));
    }

    [Fact]
    public void RotateGrab_KeepsTheSameSpotUnderThePointer()
    {
        // A 200x50 item held at (150,25): three quarters along, halfway down. Turned, it's 50x200.
        DragGeometry.RotateGrab(150f, 25f, 200f, 50f, out var x, out var y);

        Assert.Equal((37.5f, 100f), (x, y));
    }
}
