using RaidZero.Shared.Inventory;
using static Game.Shared.Tests.TestCatalog;

namespace Game.Shared.Tests;

public class InventoryValidatorTests
{
    private readonly RaidZero.Shared.Catalog.ItemCatalog _catalog = Build();

    private List<string> Codes(ItemNode root) =>
        InventoryValidator.Validate(_catalog, root).Select(e => e.Code).ToList();

    [Fact]
    public void ValidLoadout_HasNoErrors()
    {
        Assert.Empty(InventoryValidator.Validate(_catalog, ValidLoadout()));
    }

    [Fact]
    public void UnknownDefinition_IsRejected()
    {
        var root = ValidLoadout();
        root.Children[1].Children[0].DefId = "plasma_rifle";

        Assert.Equal(new[] { InventoryErrorCodes.UnknownDef }, Codes(root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void StackQuantityOutsideMaxStack_IsRejected(int quantity)
    {
        var root = ValidLoadout();
        root.Children[1].Children[1].Quantity = quantity;

        Assert.Equal(new[] { InventoryErrorCodes.QuantityOutOfRange }, Codes(root));
    }

    [Fact]
    public void NonStackableWithQuantityTwo_IsRejected()
    {
        var root = ValidLoadout();
        root.Children[1].Children[0].Quantity = 2;

        Assert.Equal(new[] { InventoryErrorCodes.QuantityOutOfRange }, Codes(root));
    }

    [Fact]
    public void MagazineOverCapacity_IsRejectedEvenWithinMaxStack()
    {
        var root = ValidLoadout();
        var rounds = root.Children[0].Children[0].Children[0];
        rounds.Quantity = 31;

        Assert.Equal(new[] { InventoryErrorCodes.QuantityOutOfRange }, Codes(root));
    }

    [Fact]
    public void ItemInASlotThatDoesNotAcceptIt_IsRejected()
    {
        var root = ValidLoadout();
        root.Children[1].Slot = "primary";

        Assert.Contains(InventoryErrorCodes.SlotMismatch, Codes(root));
    }

    [Fact]
    public void SlotTheParentDoesNotHave_IsRejected()
    {
        var root = ValidLoadout();
        root.Children[0].Slot = "tertiary";

        Assert.Equal(new[] { InventoryErrorCodes.SlotMismatch }, Codes(root));
    }

    [Fact]
    public void TwoItemsInOneNamedSlot_IsRejected()
    {
        var root = ValidLoadout();
        root.Children.Add(Node("rifle", "primary", durability: 1.0));

        Assert.Equal(new[] { InventoryErrorCodes.SlotOccupied }, Codes(root));
    }

    [Fact]
    public void GridItemOutOfBounds_IsRejected()
    {
        var root = ValidLoadout();
        root.Children[1].Children[0].X = 3; // a 2-wide item at x=3 in a 4-wide grid

        Assert.Equal(new[] { InventoryErrorCodes.OutOfBounds }, Codes(root));
    }

    [Fact]
    public void OverlappingGridItems_AreRejected()
    {
        var root = ValidLoadout();
        root.Children[1].Children[1].X = 1; // ammo onto the gpu's second cell

        Assert.Equal(new[] { InventoryErrorCodes.Overlap }, Codes(root));
    }

    [Fact]
    public void RotationSwapsTheFootprint()
    {
        var root = Node("backpack", children: [Node("gpu", "grid", 3, 0, rotated: true)]);
        Assert.Empty(Codes(root));

        root.Children[0].Rotated = false;
        Assert.Equal(new[] { InventoryErrorCodes.OutOfBounds }, Codes(root));
    }

    [Fact]
    public void GridItemWithoutPosition_IsRejected()
    {
        var root = ValidLoadout();
        root.Children[1].Children[0].Y = null;

        Assert.Equal(new[] { InventoryErrorCodes.PositionMismatch }, Codes(root));
    }

    [Fact]
    public void NamedSlotItemWithAPosition_IsRejected()
    {
        var root = ValidLoadout();
        root.Children[0].X = 0;

        Assert.Equal(new[] { InventoryErrorCodes.PositionMismatch }, Codes(root));
    }

    [Fact]
    public void RootWithASlot_IsRejected()
    {
        var root = ValidLoadout();
        root.Slot = "primary";

        Assert.Equal(new[] { InventoryErrorCodes.PositionMismatch }, Codes(root));
    }

    [Fact]
    public void GridChildOfAnItemWithoutAGrid_IsRejected()
    {
        var root = ValidLoadout();
        root.Children[0].Children.Add(Node("fuse", "grid", 0, 0));

        Assert.Equal(new[] { InventoryErrorCodes.SlotMismatch }, Codes(root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void BadDurabilityOnADurableItem_IsRejected(double? durability)
    {
        var root = ValidLoadout();
        root.Children[0].Durability = durability;

        Assert.Equal(new[] { InventoryErrorCodes.DurabilityInvalid }, Codes(root));
    }

    [Fact]
    public void DurabilityOnANonDurableItem_IsRejected()
    {
        var root = ValidLoadout();
        root.Children[1].Durability = 0.5;

        Assert.Equal(new[] { InventoryErrorCodes.DurabilityInvalid }, Codes(root));
    }

    [Fact]
    public void DuplicateIds_AreRejected()
    {
        var root = ValidLoadout();
        root.Children[1].Children[1].Id = root.Children[1].Children[0].Id;

        Assert.Equal(new[] { InventoryErrorCodes.DuplicateId }, Codes(root));
    }

    [Fact]
    public void NonCanonicalIds_AreRejected()
    {
        var root = ValidLoadout();
        root.Children[0].Id = root.Children[0].Id.ToUpperInvariant();

        Assert.Equal(new[] { InventoryErrorCodes.InvalidId }, Codes(root));
    }

    [Fact]
    public void NestingBeyondTheLimit_IsRejected()
    {
        var root = ValidLoadout(); // rifle -> magazine -> rounds is three levels below the root

        Assert.Empty(InventoryValidator.Validate(_catalog, root, maxDepth: 3));
        Assert.Equal(new[] { InventoryErrorCodes.TooDeep }, InventoryValidator.Validate(_catalog, root, maxDepth: 2).Select(e => e.Code).ToArray());
    }

    [Fact]
    public void EveryProblemIsReported_NotJustTheFirst()
    {
        var root = ValidLoadout();
        root.Children[0].Durability = null;
        root.Children[1].Children[1].Quantity = 999;

        Assert.Equal(2, Codes(root).Count);
    }
}
