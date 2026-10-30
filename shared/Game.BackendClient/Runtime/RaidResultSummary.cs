using System;
using System.Collections.Generic;
using System.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi
{
    /// <summary>
    /// What the Hideout says about a raid once it's over: a title, a sentence, and the items that came home and the
    /// ones that didn't, by name. Things inside other things are counted with them (a rifle, not the rifle, its
    /// magazine and its rounds), stacks are added up, and repeats are grouped ("AK 30-round magazine x2").
    /// </summary>
    public sealed class RaidResultSummary
    {
        private RaidResultSummary(string title, string sentence, IReadOnlyList<string> broughtHome, IReadOnlyList<string> lost, int lostCount)
        {
            Title = title;
            Sentence = sentence;
            BroughtHome = broughtHome;
            Lost = lost;
            LostCount = lostCount;
        }

        public string Title { get; }

        public string Sentence { get; }

        /// <summary>New loot that's now in the profile, grouped by name.</summary>
        public IReadOnlyList<string> BroughtHome { get; }

        /// <summary>
        /// The player's own gear that didn't come back, by name when the screen remembered what they carried in
        /// (<paramref name="carriedIn"/> on <see cref="Build"/>). Otherwise empty, and only <see cref="LostCount"/> says.
        /// </summary>
        public IReadOnlyList<string> Lost { get; }

        /// <summary>How many of their own items didn't come back, counting things inside other things.</summary>
        public int LostCount { get; }

        /// <summary>
        /// The summary of a finished raid. <paramref name="after"/> is the profile once the result is final, to name
        /// what came home; <paramref name="carriedIn"/> is the character as it was at deploy, to name what didn't
        /// (null if the screen doesn't know, after a restart, say).
        /// </summary>
        public static RaidResultSummary Build(RaidOutcome outcome, PlayerProfile after, ItemCatalog catalog, ItemNode carriedIn = null)
        {
            if (outcome == null)
            {
                throw new ArgumentNullException(nameof(outcome));
            }

            var home = new Dictionary<string, (ItemNode Node, string ParentId)>(StringComparer.Ordinal);
            Index(after?.Stash, null, home);
            Index(after?.Character, null, home);
            var broughtHome = Describe(outcome.ItemsGained, home, catalog);

            var before = new Dictionary<string, (ItemNode Node, string ParentId)>(StringComparer.Ordinal);
            Index(carriedIn, null, before);
            var lost = Describe(outcome.ItemsLost, before, catalog);

            string title;
            string sentence;
            switch (outcome.Outcome)
            {
                case "extracted":
                    title = "Extracted";
                    sentence = outcome.ItemsGained.Count == 0
                        ? "You made it out."
                        : "You made it out, and new loot came home with you.";
                    break;
                case "died":
                    title = "Lost in the zone";
                    sentence = "You didn't make it out, and everything you carried in stays there.";
                    break;
                case "refunded":
                    title = "Raid called off";
                    sentence = "The raid ended before it could count. Your gear is back.";
                    break;
                case "expired":
                    title = "You never got in";
                    sentence = "The ticket ran out before you connected, so your gear never left.";
                    break;
                default:
                    title = "Still in the zone";
                    sentence = "The raid hasn't finished yet.";
                    break;
            }

            return new RaidResultSummary(title, sentence, broughtHome, lost, outcome.ItemsLost.Count);
        }

        private static void Index(ItemNode node, string parentId, Dictionary<string, (ItemNode, string)> into)
        {
            if (node == null)
            {
                return;
            }

            if (node.Id != null)
            {
                into[node.Id] = (node, parentId);
            }

            foreach (var child in node.Children ?? new List<ItemNode>())
            {
                Index(child, node.Id, into);
            }
        }

        // The listed items that aren't inside another listed item, grouped by name: stacks add their quantities,
        // other items count copies. Items the tree doesn't know are left out.
        private static IReadOnlyList<string> Describe(IReadOnlyList<string> ids, Dictionary<string, (ItemNode Node, string ParentId)> tree, ItemCatalog catalog)
        {
            var listed = new HashSet<string>(ids ?? Array.Empty<string>(), StringComparer.Ordinal);
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var id in listed)
            {
                if (!tree.TryGetValue(id, out var entry) || (entry.ParentId != null && listed.Contains(entry.ParentId)))
                {
                    continue;
                }

                ItemDefinition def = null;
                catalog?.TryGet(entry.Node.DefId, out def);
                var name = def != null && !string.IsNullOrEmpty(def.DisplayName) ? def.DisplayName : entry.Node.DefId;
                var amount = def != null && def.MaxStack > 1 ? Math.Max(1, entry.Node.Quantity) : 1;
                if (!totals.ContainsKey(name))
                {
                    order.Add(name);
                    totals[name] = 0;
                }

                totals[name] += amount;
            }

            return order
                .OrderByDescending(name => totals[name])
                .ThenBy(name => name, StringComparer.Ordinal)
                .Select(name => totals[name] > 1 ? name + " x" + totals[name] : name)
                .ToList();
        }
    }
}
