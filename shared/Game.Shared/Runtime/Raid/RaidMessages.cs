using System.Collections.Generic;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.Raid
{
    /// <summary>What kind of root container something is in a raid, which decides who may touch it.</summary>
    public enum RootKind : byte
    {
        /// <summary>A living player's gear. Only that player can touch it.</summary>
        Character = 1,

        /// <summary>A dead player's gear. Anyone can open and loot it.</summary>
        Body = 2,

        /// <summary>A crate or other loot container placed in the level.</summary>
        Container = 3,

        /// <summary>One dropped item lying on the ground. Disappears once it's empty.</summary>
        Ground = 4,
    }

    /// <summary>Error codes the raid adds to <see cref="InventoryErrorCodes"/>.</summary>
    public static class RaidInventoryErrorCodes
    {
        /// <summary>The player isn't in the raid, or already died or extracted.</summary>
        public const string PlayerNotActive = "player_not_active";

        /// <summary>The item or the target is in a container the player hasn't opened, or that's too far away.</summary>
        public const string OutOfReach = "out_of_reach";

        /// <summary>Not something a player can do in a raid, such as destroying an item.</summary>
        public const string NotAllowed = "not_allowed";

        /// <summary>The client sent requests faster than the server accepts them. Nothing was done.</summary>
        public const string RateLimited = "rate_limited";
    }

    /// <summary>
    /// One message in the raid inventory conversation between a client and the game server. Encode and
    /// decode them with <see cref="RaidProtocol"/>; the network layer just carries the bytes.
    ///
    /// Client to server: <see cref="InventoryRequest"/>, <see cref="PickUpRequest"/>, <see cref="DropRequest"/>,
    /// <see cref="OpenRequest"/>, <see cref="CloseRequest"/>. Each but Close carries a request ID, and the
    /// server answers it with one <see cref="RequestResult"/>, after any views the request changed.
    ///
    /// Server to client: <see cref="ContainerView"/> (here's what's in a container now) and
    /// <see cref="ContainerClosed"/> (you can't see that container any more), plus the results.
    /// </summary>
    public abstract class RaidMessage
    {
    }

    /// <summary>A client message the server answers with a <see cref="RequestResult"/> carrying the same ID.</summary>
    public abstract class RaidRequest : RaidMessage
    {
        /// <summary>Chosen by the client, unique among its requests in flight.</summary>
        public uint RequestId { get; set; }
    }

    /// <summary>Move, split or merge, as one all-or-nothing batch.</summary>
    public sealed class InventoryRequest : RaidRequest
    {
        public List<InventoryOp> Ops { get; set; } = new List<InventoryOp>();
    }

    /// <summary>Take an item from the ground or an open container, into the first place it fits.</summary>
    public sealed class PickUpRequest : RaidRequest
    {
        public string ItemId { get; set; }
    }

    /// <summary>Drop one of your items on the ground.</summary>
    public sealed class DropRequest : RaidRequest
    {
        public string ItemId { get; set; }
    }

    /// <summary>Start looting a crate, a body or a pile on the ground. The server checks it's within reach.</summary>
    public sealed class OpenRequest : RaidRequest
    {
        public string ContainerId { get; set; }
    }

    /// <summary>Stop looting a container. Not answered.</summary>
    public sealed class CloseRequest : RaidMessage
    {
        public string ContainerId { get; set; }
    }

    /// <summary>The answer to one request.</summary>
    public sealed class RequestResult : RaidMessage
    {
        public uint RequestId { get; set; }

        public bool Success { get; set; }

        /// <summary>One of InventoryErrorCodes or RaidInventoryErrorCodes; null on success.</summary>
        public string Error { get; set; }

        /// <summary>For an <see cref="InventoryRequest"/>: which op was refused, if one was.</summary>
        public int? FailedOpIndex { get; set; }
    }

    /// <summary>A container's full contents as they are now. Replaces whatever the client showed for it.</summary>
    public sealed class ContainerView : RaidMessage
    {
        public RootKind Kind { get; set; }

        /// <summary>The container itself, with everything in it.</summary>
        public ItemNode Root { get; set; }
    }

    /// <summary>The client can't see this container any more: it's gone, or out of reach.</summary>
    public sealed class ContainerClosed : RaidMessage
    {
        public string ContainerId { get; set; }
    }
}
