using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;

namespace RaidZero.Shared.InventoryUi
{
    /// <summary>The screen's own refusal codes, and a sentence for the player for any code it can be shown.</summary>
    public static class InventoryUiErrors
    {
        /// <summary>The two items don't combine: different items, and neither fits in the other.</summary>
        public const string CantCombine = "cant_combine";

        /// <summary>The item is part of a request still waiting for the server.</summary>
        public const string Pending = "pending";

        /// <summary>Quick move found nowhere to put it.</summary>
        public const string NowhereToGo = "nowhere_to_go";

        /// <summary>This screen can't drop things (the Hideout stash has no ground).</summary>
        public const string CantDrop = "cant_drop";

        /// <summary>Shift-dragged part of a stack out of the window: only whole items go on the ground.</summary>
        public const string CantDropPart = "cant_drop_part";

        public static string Describe(string code)
        {
            switch (code)
            {
                case null:
                    return null;
                case InventoryErrorCodes.Overlap:
                    return "That space is taken.";
                case InventoryErrorCodes.OutOfBounds:
                    return "It doesn't fit there.";
                case InventoryErrorCodes.NoSpace:
                    return "There's no room for it.";
                case InventoryErrorCodes.SlotMismatch:
                    return "That doesn't go there.";
                case InventoryErrorCodes.SlotOccupied:
                    return "Something's already there.";
                case InventoryErrorCodes.StackFull:
                    return "That stack is full.";
                case InventoryErrorCodes.QuantityOutOfRange:
                    return "Too many to fit there.";
                case InventoryErrorCodes.Cycle:
                    return "It can't go inside itself.";
                case InventoryErrorCodes.TooDeep:
                    return "Too many containers inside containers.";
                case InventoryErrorCodes.ItemNotFound:
                case InventoryErrorCodes.TargetNotFound:
                    return "It's not there any more.";
                case InventoryErrorCodes.RootItem:
                    return "That can't be moved.";
                case RaidInventoryErrorCodes.OutOfReach:
                    return "That's out of reach.";
                case RaidInventoryErrorCodes.RateLimited:
                    return "Too fast. Try again.";
                case RaidInventoryErrorCodes.PlayerNotActive:
                    return "You're not in the raid any more.";
                case RaidInventoryErrorCodes.NotAllowed:
                    return "You can't do that here.";
                // From the backend, for Hideout edits (PlayerSession).
                case "in_raid":
                    return "Your stash is locked while you're in a raid.";
                case "version_conflict":
                    return "Your stash changed somewhere else. Take another look.";
                case "backend_unreachable":
                    return "Can't reach the server. Try again in a moment.";
                case "logged_out":
                case "not_logged_in":
                    return "You're not logged in.";
                case CantCombine:
                    return "Those don't go together.";
                case Pending:
                    return "Still waiting on the last move.";
                case NowhereToGo:
                    return "There's nowhere to put it.";
                case CantDrop:
                    return "You can't drop things here.";
                case CantDropPart:
                    return "Drop the whole stack, or split it off first.";
                case InventoryErrorCodes.InvalidQuantity:
                    return "You can't split off that many.";
                default:
                    return "That didn't work (" + code + ").";
            }
        }
    }
}
