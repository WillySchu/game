using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Trading;

namespace RaidZero.BackendApi
{
    /// <summary>
    /// What the player has put up for sale on the Hideout's trader screen, before they press Sell. Items stay
    /// where they are until the sale goes through. Each one is checked as it's added, with the same rules the
    /// backend uses (TradePlanner), so the list only ever holds a sale that would work right now: money, items
    /// with money in them, and things the trader doesn't buy are refused with a sentence saying why. Listing a
    /// container takes anything in it off the list, as it sells with the container.
    /// </summary>
    public sealed class TraderSellList
    {
        private readonly ItemCatalog _catalog;
        private readonly List<string> _ids = new List<string>();

        public TraderSellList(ItemCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        /// <summary>Raised whenever the list changes.</summary>
        public event Action Changed;

        public IReadOnlyList<string> ItemIds => _ids;

        public int Count => _ids.Count;

        public bool Contains(string itemId) => itemId != null && _ids.Contains(itemId);

        /// <summary>Lists an item. Null if it's on the list now; otherwise why not, as a sentence.</summary>
        public string Add(PlayerProfile profile, double buysAt, string itemId)
        {
            if (Contains(itemId))
            {
                return null;
            }

            var problem = Try(profile, buysAt, itemId, out var candidate);
            if (problem != null)
            {
                return problem;
            }

            _ids.Clear();
            _ids.AddRange(candidate);
            Changed?.Invoke();
            return null;
        }

        /// <summary>Why the item couldn't be listed, as a sentence; null if it could (or already is). Changes nothing.</summary>
        public string WhyNot(PlayerProfile profile, double buysAt, string itemId) =>
            Contains(itemId) ? null : Try(profile, buysAt, itemId, out _);

        private string Try(PlayerProfile profile, double buysAt, string itemId, out List<string> candidate)
        {
            candidate = null;
            if (profile == null)
            {
                return "Log in first.";
            }

            var item = Find(profile, itemId);
            if (item == null)
            {
                return "That item isn't in your stash or gear any more.";
            }

            // Anything already listed inside it now goes with it.
            var inside = new HashSet<string>(item.Walk().Select(n => n.Id));
            candidate = _ids.Where(id => !inside.Contains(id)).ToList();
            candidate.Add(itemId);
            var plan = Check(profile, buysAt, candidate);
            return plan.Success ? null : plan.Message;
        }

        public void Remove(string itemId)
        {
            if (_ids.Remove(itemId))
            {
                Changed?.Invoke();
            }
        }

        public void Clear()
        {
            if (_ids.Count > 0)
            {
                _ids.Clear();
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// After the profile changed (a sale, an edit, a refresh): drops what's no longer in the stash or gear, or
        /// what can no longer be sold (money moved into a listed backpack, say), keeping the rest in order.
        /// </summary>
        public void Prune(PlayerProfile profile, double buysAt)
        {
            if (_ids.Count == 0)
            {
                return;
            }

            var kept = new List<string>();
            foreach (var id in _ids)
            {
                if (profile != null && Find(profile, id) != null && Check(profile, buysAt, kept.Concat(new[] { id }).ToList()).Success)
                {
                    kept.Add(id);
                }
            }

            if (kept.Count != _ids.Count)
            {
                _ids.Clear();
                _ids.AddRange(kept);
                Changed?.Invoke();
            }
        }

        /// <summary>What the trader pays for one listed item, with everything in it; 0 if it's gone.</summary>
        public long PriceOf(PlayerProfile profile, double buysAt, string itemId)
        {
            var item = profile != null ? Find(profile, itemId) : null;
            return item != null ? TraderPrices.SellPrice(_catalog, item, buysAt) : 0;
        }

        /// <summary>What the whole list sells for.</summary>
        public long Total(PlayerProfile profile, double buysAt) => _ids.Sum(id => PriceOf(profile, buysAt, id));

        /// <summary>The item in the player's stash or gear with that ID, or null.</summary>
        public static ItemNode Find(PlayerProfile profile, string itemId)
        {
            if (profile == null || itemId == null)
            {
                return null;
            }

            foreach (var root in new[] { profile.Stash, profile.Character })
            {
                if (root == null)
                {
                    continue;
                }

                foreach (var node in root.Walk())
                {
                    if (node.Id == itemId)
                    {
                        return node;
                    }
                }
            }

            return null;
        }

        // The planner changes the trees it's given, so it gets copies.
        private TradePlan Check(PlayerProfile profile, double buysAt, IReadOnlyList<string> ids) =>
            TradePlanner.PlanSale(_catalog, profile.Stash.Clone(), profile.Character?.Clone(), ids, buysAt);
    }

    /// <summary>The trader screen's words and numbers.</summary>
    public static class TraderText
    {
        /// <summary>"18,050 cr".</summary>
        public static string Credits(long credits) => credits.ToString("#,0", CultureInfo.InvariantCulture) + " cr";

        /// <summary>"3 h 5 min", "40 min", or "a moment" for under a minute.</summary>
        public static string Wait(TimeSpan wait)
        {
            if (wait.TotalMinutes < 1)
            {
                return "a moment";
            }

            var minutes = (long)Math.Ceiling(wait.TotalMinutes);
            return minutes >= 60 ? (minutes / 60) + " h " + (minutes % 60) + " min" : minutes + " min";
        }

        /// <summary>"2 left", "Sold out · more in 3 h 5 min", or null when there's no limit.</summary>
        public static string Stock(TraderOfferInfo offer, DateTimeOffset now)
        {
            if (offer?.Left == null)
            {
                return null;
            }

            if (offer.Left.Value > 0)
            {
                return offer.Left.Value + " left";
            }

            return offer.RestocksAt.HasValue ? "Sold out · more in " + Wait(offer.RestocksAt.Value - now) : "Sold out";
        }

        /// <summary>The offer's name: "Makarov PM", or "5.45x39 PS ×60" for a stack.</summary>
        public static string Title(ItemCatalog catalog, ItemNode item)
        {
            var name = NameOf(catalog, item?.DefId);
            return item != null && item.Quantity > 1 ? name + " ×" + item.Quantity : name;
        }

        /// <summary>What comes with it: "with PM magazine", or null when nothing does.</summary>
        public static string Contents(ItemCatalog catalog, ItemNode item)
        {
            if (item == null || item.Children.Count == 0)
            {
                return null;
            }

            return "with " + string.Join(", ", item.Children.Select(c => Title(catalog, c)));
        }

        /// <summary>A reason the backend gave, as a sentence when it sent none.</summary>
        public static string Refusal(TradeOutcome outcome)
        {
            if (outcome == null || outcome.Succeeded)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(outcome.Message))
            {
                return outcome.Message;
            }

            switch (outcome.Error)
            {
                case "in_raid":
                    return "Your gear is locked while you're in a raid.";
                case "version_conflict":
                    return "Your stash kept changing. Try again.";
                default:
                    return "The trade didn't go through (" + outcome.Error + ").";
            }
        }

        private static string NameOf(ItemCatalog catalog, string defId) =>
            defId != null && catalog != null && catalog.TryGet(defId, out var definition) && !string.IsNullOrEmpty(definition.DisplayName)
                ? definition.DisplayName
                : defId;
    }
}
