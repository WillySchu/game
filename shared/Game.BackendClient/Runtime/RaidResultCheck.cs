using System.Collections.Generic;
using System.Linq;
using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi
{
    /// <summary>
    /// What CI's scripted client checks once its raid is over: that the backend recorded the way it left, and
    /// that the gear it went in with ended up where that way out says it should. Kept out of Unity so the rules
    /// have tests of their own.
    /// </summary>
    public static class RaidResultCheck
    {
        /// <summary>
        /// The IDs of every item the player is carrying: everything under the character container, not the
        /// container itself, which never leaves the profile.
        /// </summary>
        public static HashSet<string> CarriedItemIds(PlayerProfile profile)
        {
            var ids = new HashSet<string>();
            if (profile?.Character?.Children != null)
            {
                foreach (var child in profile.Character.Children)
                {
                    Collect(child, ids);
                }
            }

            return ids;
        }

        /// <summary>
        /// Null if the result is right; otherwise what's wrong, in a sentence.
        /// <paramref name="extracted"/> is what the test expected: true for an extraction, false for a death
        /// (missing in action counts as one). <paramref name="carriedIn"/> is <see cref="CarriedItemIds"/> from
        /// before the raid, and <paramref name="after"/> is the profile once the result is final.
        /// </summary>
        public static string Problem(bool extracted, ISet<string> carriedIn, RaidOutcome outcome, PlayerProfile after)
        {
            if (outcome == null)
            {
                return "There's no raid result.";
            }

            if (carriedIn == null || carriedIn.Count == 0)
            {
                return "The player went in with no gear, so there's nothing to check the result against.";
            }

            var expectedOutcome = extracted ? "extracted" : "died";
            if (outcome.Outcome != expectedOutcome)
            {
                return "The backend recorded the raid as " + (outcome.Outcome ?? "(nothing)") + ", not " + expectedOutcome + ".";
            }

            var owned = new HashSet<string>();
            if (after?.Stash != null)
            {
                Collect(after.Stash, owned);
            }

            if (after?.Character != null)
            {
                Collect(after.Character, owned);
            }

            if (extracted)
            {
                if (outcome.ItemsLost.Count > 0)
                {
                    return "The player extracted, but the result lists " + outcome.ItemsLost.Count + " item(s) as lost.";
                }

                var missing = carriedIn.Where(id => !owned.Contains(id)).ToList();
                if (missing.Count > 0)
                {
                    return missing.Count + " of the " + carriedIn.Count + " item(s) the player went in with aren't in their profile after extracting, such as "
                        + missing[0] + ".";
                }
            }
            else
            {
                if (outcome.ItemsReturned.Count > 0)
                {
                    return "The player died, but the result lists " + outcome.ItemsReturned.Count + " item(s) as returned.";
                }

                if (outcome.ItemsLost.Count == 0)
                {
                    return "The player died, but the result lists nothing as lost.";
                }

                var kept = carriedIn.Where(owned.Contains).ToList();
                if (kept.Count > 0)
                {
                    return kept.Count + " of the " + carriedIn.Count + " item(s) the player died with are still in their profile, such as " + kept[0] + ".";
                }
            }

            return null;
        }

        private static void Collect(ItemNode node, HashSet<string> ids)
        {
            ids.Add(node.Id);
            if (node.Children == null)
            {
                return;
            }

            foreach (var child in node.Children)
            {
                Collect(child, ids);
            }
        }
    }
}
