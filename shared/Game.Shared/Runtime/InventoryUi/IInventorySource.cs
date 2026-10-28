using System;
using System.Collections.Generic;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.InventoryUi
{
    /// <summary>What a container on the inventory screen is, which decides where the screen draws it.</summary>
    public enum InventoryRootKind
    {
        /// <summary>The player's own gear: always on the left.</summary>
        Character,

        /// <summary>The player's stash, in the Hideout.</summary>
        Stash,

        /// <summary>A crate or other container in the level.</summary>
        Container,

        /// <summary>A dead player's gear.</summary>
        Body,

        /// <summary>Items lying on the ground.</summary>
        Ground,
    }

    /// <summary>One container the screen shows, with everything in it.</summary>
    public sealed class InventoryRoot
    {
        public InventoryRoot(InventoryRootKind kind, ItemNode root)
        {
            Kind = kind;
            Root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public InventoryRootKind Kind { get; }

        public ItemNode Root { get; }
    }

    /// <summary>
    /// Where the inventory screen gets its items and sends its changes. The raid's version talks to the game
    /// server; the Hideout's will talk to the backend; a fake one lets the screen run without either. The screen
    /// never changes items itself: it sends ops and waits for the source to report the new state.
    /// </summary>
    public interface IInventorySource
    {
        /// <summary>Anything on screen changed: contents, which containers are open, or what's pending.</summary>
        event Action Changed;

        /// <summary>A request was refused, with its error code (InventoryErrorCodes, RaidInventoryErrorCodes and so on).</summary>
        event Action<string> Refused;

        /// <summary>The containers to show: the player's character, plus whatever else is open.</summary>
        IReadOnlyList<InventoryRoot> Roots { get; }

        /// <summary>Whether an item is part of a request still waiting for an answer. The screen dims it.</summary>
        bool IsPending(string itemId);

        /// <summary>Whether <see cref="Drop"/> is possible here (the raid has ground; the stash doesn't).</summary>
        bool CanDrop { get; }

        /// <summary>Applies the ops as one all-or-nothing batch.</summary>
        void Send(IReadOnlyList<InventoryOp> ops);

        /// <summary>Drops the item on the ground.</summary>
        void Drop(string itemId);

        /// <summary>Stops showing a container the player opened.</summary>
        void Close(string rootId);
    }
}
