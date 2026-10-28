using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;
using static Game.Shared.Tests.TestCatalog;

namespace Game.Shared.Tests;

public class RaidProtocolTests
{
    private static readonly string A = Uuid7.NewId();
    private static readonly string B = Uuid7.NewId();

    private static readonly string[] Names = ["inventory", "pickup", "drop", "open", "close", "result_ok", "result_failed", "view", "closed"];

    public static TheoryData<string> Messages
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Names)
            {
                data.Add(name);
            }

            return data;
        }
    }

    private static RaidMessage Build(string name) => name switch
    {
        "inventory" => new InventoryRequest
        {
            RequestId = 7,
            Ops =
            [
                InventoryOp.Move(A, InventoryTarget.Grid(B, 3, 4, rotated: true)),
                InventoryOp.Move(A, InventoryTarget.NamedSlot(B, "primary")),
                InventoryOp.Move(A, InventoryTarget.AnyGridSpot(B)),
                InventoryOp.Split(A, 15, InventoryTarget.Grid(B, 0, 1)),
                InventoryOp.Merge(A, B, 0),
                InventoryOp.Discard(A),
            ],
        },
        "pickup" => new PickUpRequest { RequestId = uint.MaxValue, ItemId = A },
        "drop" => new DropRequest { RequestId = 1, ItemId = A },
        "open" => new OpenRequest { RequestId = 2, ContainerId = B },
        "close" => new CloseRequest { ContainerId = B },
        "result_ok" => new RequestResult { RequestId = 3, Success = true },
        "result_failed" => new RequestResult { RequestId = 4, Success = false, Error = "slot_occupied", FailedOpIndex = 2 },
        "view" => new ContainerView { Kind = RootKind.Body, Root = ValidLoadout() },
        "closed" => new ContainerClosed { ContainerId = A },
        _ => throw new ArgumentException(name),
    };

    private static bool FromClient(RaidMessage message) => message is RaidRequest or CloseRequest;

    private static RaidMessage Decode(RaidMessage original, byte[] bytes) =>
        FromClient(original) ? RaidProtocol.DecodeFromClient(bytes) : RaidProtocol.DecodeFromServer(bytes);

    [Theory]
    [MemberData(nameof(Messages))]
    public void EveryMessage_SurvivesTheRoundTrip(string name)
    {
        var original = Build(name);
        var bytes = RaidProtocol.Encode(original);

        var decoded = Decode(original, bytes);

        Assert.IsType(original.GetType(), decoded);
        Assert.Equal(bytes, RaidProtocol.Encode(decoded));
    }

    [Fact]
    public void Ops_KeepEveryField()
    {
        var decoded = (InventoryRequest)RaidProtocol.DecodeFromClient(RaidProtocol.Encode(Build("inventory")));

        Assert.Equal(7u, decoded.RequestId);
        var move = decoded.Ops[0];
        Assert.Equal((InventoryOpKinds.Move, A, B, SlotNames.Grid, 3, 4, true), (move.Op, move.ItemId, move.To.ParentId, move.To.Slot, move.To.X!.Value, move.To.Y!.Value, move.To.Rotated));
        Assert.Null(decoded.Ops[2].To.X);
        Assert.Equal("primary", decoded.Ops[1].To.Slot);
        Assert.Equal((InventoryOpKinds.Split, 15), (decoded.Ops[3].Op, decoded.Ops[3].Quantity));
        Assert.Equal((InventoryOpKinds.Merge, B), (decoded.Ops[4].Op, decoded.Ops[4].IntoId));
        Assert.Equal(InventoryOpKinds.Discard, decoded.Ops[5].Op);
    }

    [Fact]
    public void AContainer_ComesBackItemForItem()
    {
        var original = ValidLoadout();

        var view = (ContainerView)RaidProtocol.DecodeFromServer(RaidProtocol.Encode(new ContainerView { Kind = RootKind.Character, Root = original }));

        Assert.Equal(RootKind.Character, view.Kind);
        var expected = original.Walk().ToList();
        var actual = view.Root.Walk().ToList();
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(
                (expected[i].Id, expected[i].DefId, expected[i].Slot, expected[i].X, expected[i].Y, expected[i].Rotated, expected[i].Quantity, expected[i].Children.Count),
                (actual[i].Id, actual[i].DefId, actual[i].Slot, actual[i].X, actual[i].Y, actual[i].Rotated, actual[i].Quantity, actual[i].Children.Count));
            Assert.Equal(expected[i].Durability ?? -1, actual[i].Durability ?? -1, 5);
        }
    }

    [Fact]
    public void Ids_TravelAs16Bytes()
    {
        // Version, type, request ID, then the item ID.
        Assert.Equal(1 + 1 + 4 + 16, RaidProtocol.Encode(Build("pickup")).Length);
    }

    [Fact]
    public void Sending_RefusesIdsThatArentUuids()
    {
        Assert.Throws<ArgumentException>(() => RaidProtocol.Encode(new PickUpRequest { ItemId = "not-an-id" }));
        Assert.Throws<ArgumentException>(() => RaidProtocol.Encode(new PickUpRequest { ItemId = A.ToUpperInvariant() }));
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void EachSide_OnlyAcceptsTheOthersMessages(string name)
    {
        var original = Build(name);
        var bytes = RaidProtocol.Encode(original);

        if (FromClient(original))
        {
            Assert.Throws<ProtocolException>(() => RaidProtocol.DecodeFromServer(bytes));
        }
        else
        {
            Assert.Throws<ProtocolException>(() => RaidProtocol.DecodeFromClient(bytes));
        }
    }

    [Fact]
    public void ACutShortMessage_IsRefusedAtEveryLength()
    {
        var bytes = RaidProtocol.Encode(Build("inventory"));

        for (int length = 0; length < bytes.Length; length++)
        {
            Assert.Throws<ProtocolException>(() => RaidProtocol.DecodeFromClient(bytes.Take(length).ToArray()));
        }
    }

    [Fact]
    public void ExtraBytes_WrongVersions_AndUnknownTypes_AreRefused()
    {
        var bytes = RaidProtocol.Encode(Build("pickup"));

        Assert.Throws<ProtocolException>(() => RaidProtocol.DecodeFromClient([.. bytes, 0]));
        Assert.Throws<ProtocolException>(() => RaidProtocol.DecodeFromClient([2, .. bytes.Skip(1)]));
        Assert.Throws<ProtocolException>(() => RaidProtocol.DecodeFromClient([1, 99, .. bytes.Skip(2)]));
        Assert.Throws<ProtocolException>(() => RaidProtocol.DecodeFromClient(null!));
    }

    [Fact]
    public void Limits_HoldAgainstHandCraftedMessages()
    {
        var oneOp = RaidProtocol.Encode(new InventoryRequest { RequestId = 1, Ops = [InventoryOp.Discard(A)] });

        // Op count (byte 6) claiming more than the limit, or none.
        var tooMany = oneOp.ToArray();
        tooMany[6] = RaidProtocol.MaxOpsPerRequest + 1;
        Assert.Throws<ProtocolException>(() => RaidProtocol.DecodeFromClient(tooMany));
        var none = oneOp.ToArray();
        none[6] = 0;
        Assert.Throws<ProtocolException>(() => RaidProtocol.DecodeFromClient(none));

        // A slot name longer than allowed.
        var move = RaidProtocol.Encode(new InventoryRequest { RequestId = 1, Ops = [InventoryOp.Move(A, InventoryTarget.NamedSlot(B, "x"))] });
        var slotLength = 1 + 1 + 4 + 1 + 1 + 16 + 16;
        Assert.Equal(1, move[slotLength]);
        var longSlot = move.ToArray();
        longSlot[slotLength] = RaidProtocol.MaxStringBytes + 1;
        Assert.Throws<ProtocolException>(() => RaidProtocol.DecodeFromClient(longSlot));

        // Anything over the client size limit.
        Assert.Throws<ProtocolException>(() => RaidProtocol.DecodeFromClient(new byte[RaidProtocol.MaxClientMessageBytes + 1]));
    }

    [Fact]
    public void AContainerWithAnImpossibleParent_IsRefused()
    {
        var bytes = RaidProtocol.Encode(new ContainerView { Kind = RootKind.Container, Root = ValidLoadout() });

        // The second item's parent index sits right after its 16-byte ID: version, type, kind, count,
        // then the root (16 ID, 2 parent, 1+9 "character", 1 null slot, 1 flags, 4 quantity).
        var secondParent = 1 + 1 + 1 + 2 + (16 + 2 + 10 + 1 + 1 + 4) + 16;
        Assert.Equal(0, BitConverter.ToUInt16(bytes, secondParent));
        var self = bytes.ToArray();
        self[secondParent] = 1;
        Assert.Throws<ProtocolException>(() => RaidProtocol.DecodeFromServer(self));
    }

    [Fact]
    public void RandomBytes_OnlyEverFailAsProtocolErrors()
    {
        var random = new Random(1234);
        var samples = Names.Select(name => RaidProtocol.Encode(Build(name))).ToList();
        for (int i = 0; i < 20_000; i++)
        {
            byte[] data;
            if (i % 2 == 0)
            {
                data = new byte[random.Next(0, 64)];
                random.NextBytes(data);
                if (data.Length > 1 && i % 4 == 0)
                {
                    data[0] = RaidProtocol.Version;
                }
            }
            else
            {
                data = samples[random.Next(samples.Count)].ToArray();
                for (int flips = random.Next(1, 4); flips > 0; flips--)
                {
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                }
            }

            foreach (var decode in new Func<byte[], RaidMessage>[] { RaidProtocol.DecodeFromClient, RaidProtocol.DecodeFromServer })
            {
                try
                {
                    decode(data);
                }
                catch (ProtocolException)
                {
                }
            }
        }
    }
}
