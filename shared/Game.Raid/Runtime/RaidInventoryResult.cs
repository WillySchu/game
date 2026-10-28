using System.Collections.Generic;
using RaidZero.Shared.Inventory;

namespace RaidZero.Raid
{
    /// <summary>What kind of root container something is, which decides who may touch it.</summary>
    public enum RootKind
    {
        /// <summary>A living player's gear. Only that player can touch it.</summary>
        Character,

        /// <summary>A dead player's gear. Anyone can open and loot it.</summary>
        Body,

        /// <summary>A crate or other loot container placed in the level.</summary>
        Container,

        /// <summary>One dropped item lying on the ground. Disappears once it's empty.</summary>
        Ground,
    }

    /// <summary>
    /// The outcome of one request, and what to tell everyone about it.
    ///
    /// <see cref="Updates"/> is the part to send over the network: for every player who can see a root
    /// container the request changed, the container's new contents. Sending whole containers rather than
    /// item-level diffs keeps clients simple (replace the view, no patching), and containers are small.
    /// </summary>
    public sealed class RaidInventoryResult
    {
        private static readonly IReadOnlyList<RootUpdate> NoUpdates = new RootUpdate[0];
        private static readonly IReadOnlyList<string> NoRoots = new string[0];

        private RaidInventoryResult(
            bool success,
            InventoryError error,
            int? failedOpIndex,
            ChangeSet changes,
            IReadOnlyList<CreatedItem> created,
            IReadOnlyList<RootUpdate> updates,
            IReadOnlyList<string> createdRoots,
            IReadOnlyList<string> removedRoots)
        {
            Success = success;
            Error = error;
            FailedOpIndex = failedOpIndex;
            Changes = changes;
            Created = created;
            Updates = updates;
            CreatedRoots = createdRoots;
            RemovedRoots = removedRoots;
        }

        public bool Success { get; }

        /// <summary>Why the request was refused; its Code is one of InventoryErrorCodes or RaidInventoryErrorCodes.</summary>
        public InventoryError Error { get; }

        /// <summary>For <see cref="RaidInventoryService.Apply"/>: which op was refused, when one was.</summary>
        public int? FailedOpIndex { get; }

        /// <summary>The item-level effect, for logging and tests. Empty on failure.</summary>
        public ChangeSet Changes { get; }

        /// <summary>Items a split created.</summary>
        public IReadOnlyList<CreatedItem> Created { get; }

        /// <summary>What each affected player should now see. Empty on failure.</summary>
        public IReadOnlyList<RootUpdate> Updates { get; }

        /// <summary>Piles that appeared on the ground (a drop). Spawn a pickup in the world for each.</summary>
        public IReadOnlyList<string> CreatedRoots { get; }

        /// <summary>Piles that emptied and are gone. Despawn their pickups.</summary>
        public IReadOnlyList<string> RemovedRoots { get; }

        public override string ToString() => Success ? "ok" : "refused: " + Error;

        internal static RaidInventoryResult Failed(InventoryError error, int? failedOpIndex = null) =>
            new RaidInventoryResult(false, error, failedOpIndex, new ChangeSet(), new CreatedItem[0], NoUpdates, NoRoots, NoRoots);

        internal static RaidInventoryResult Succeeded(
            ApplyResult applied,
            IReadOnlyList<RootUpdate> updates,
            IReadOnlyList<string> createdRoots,
            IReadOnlyList<string> removedRoots) =>
            new RaidInventoryResult(true, null, null, applied?.Changes ?? new ChangeSet(), applied?.Created ?? new CreatedItem[0],
                updates ?? NoUpdates, createdRoots ?? NoRoots, removedRoots ?? NoRoots);
    }

    /// <summary>One player's new view of one root container.</summary>
    public sealed class RootUpdate
    {
        internal RootUpdate(string playerId, string rootId, ItemNode root)
        {
            PlayerId = playerId;
            RootId = rootId;
            Root = root;
        }

        public string PlayerId { get; }

        public string RootId { get; }

        /// <summary>
        /// The container and everything in it, as it is now; a copy, shared by every update for the same
        /// container in one result, so don't modify it. Null when the player can no longer see the
        /// container: it's gone, or it closed for them.
        /// </summary>
        public ItemNode Root { get; }

        public bool IsRemoved => Root == null;
    }

    /// <summary>Error codes the raid adds to <see cref="InventoryErrorCodes"/>.</summary>
    public static class RaidInventoryErrorCodes
    {
        /// <summary>The player isn't in the raid, or already died or extracted.</summary>
        public const string PlayerNotActive = "player_not_active";

        /// <summary>The item or the target is in a container the player hasn't opened.</summary>
        public const string OutOfReach = "out_of_reach";

        /// <summary>Not something a player can do in a raid, such as destroying an item.</summary>
        public const string NotAllowed = "not_allowed";
    }
}
