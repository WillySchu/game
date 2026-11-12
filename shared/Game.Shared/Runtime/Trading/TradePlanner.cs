using System;
using System.Collections.Generic;
using System.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.Trading
{
    /// <summary>Why a trade was refused, as the backend reports it (the "reason" of an invalid_trade).</summary>
    public static class TradeErrorCodes
    {
        public const string UnknownTrader = "unknown_trader";
        public const string UnknownOffer = "unknown_offer";

        /// <summary>A count below 1 or above the limit, or nothing listed to sell.</summary>
        public const string InvalidCount = "invalid_count";

        /// <summary>The offer has fewer left until it restocks than asked for.</summary>
        public const string OutOfStock = "out_of_stock";

        public const string NotEnoughMoney = "not_enough_money";

        /// <summary>No room in the stash for what was bought, or for the money from a sale.</summary>
        public const string NoSpace = InventoryErrorCodes.NoSpace;

        public const string ItemNotFound = InventoryErrorCodes.ItemNotFound;

        /// <summary>The trader doesn't buy it: money, a stash or character, or something worth nothing.</summary>
        public const string NotForSale = "not_for_sale";

        /// <summary>It has money inside, which would be sold for half its worth. Take it out first.</summary>
        public const string MoneyInside = "money_inside";

        /// <summary>The same item twice, or an item and something it's inside.</summary>
        public const string ListedTwice = "listed_twice";
    }

    /// <summary>
    /// What a trade changes in a player's stash and gear: which items go, which money stacks change, and the
    /// new items placed in the stash. The backend writes exactly this; on failure, why.
    /// </summary>
    public sealed class TradePlan
    {
        private TradePlan()
        {
        }

        public bool Success { get; private set; }

        /// <summary>One of <see cref="TradeErrorCodes"/> when it failed.</summary>
        public string Error { get; private set; }

        public string Message { get; private set; }

        /// <summary>The item the refusal is about, when there is one.</summary>
        public string ItemId { get; private set; }

        /// <summary>Credits paid out (a sale) or spent (a purchase).</summary>
        public long Credits { get; private set; }

        /// <summary>Items sold, and everything that was inside them.</summary>
        public List<string> Sold { get; } = new List<string>();

        /// <summary>Money stacks used up whole by a purchase.</summary>
        public List<string> Spent { get; } = new List<string>();

        /// <summary>Money stacks whose quantity changed; their nodes hold the new quantity.</summary>
        public List<ItemNode> Updated { get; } = new List<ItemNode>();

        /// <summary>New items, placed in the stash (in its top level): what was bought, or the money from a sale.</summary>
        public List<ItemNode> Added { get; } = new List<ItemNode>();

        internal static TradePlan Failed(string error, string message, string itemId = null) =>
            new TradePlan { Error = error, Message = message, ItemId = itemId };

        internal static TradePlan Started() => new TradePlan { Success = true };

        internal TradePlan Done(long credits)
        {
            Credits = credits;
            return this;
        }

        public override string ToString() => Success ? "ok, " + Credits + " credits" : Error + ": " + Message;
    }

    /// <summary>
    /// Works out trades with a trader against a player's stash and character: selling items for credits, and
    /// buying items with them. Money lives in the stash as credit stacks, anywhere inside it: a purchase is paid
    /// from the smallest stacks first, so change collects into fewer, and a sale's money tops up existing stacks
    /// before starting new ones. New items go in the first free spot in the stash's top level.
    ///
    /// It changes the trees it's given, even when the trade fails part-way: pass copies to preview a trade.
    /// </summary>
    public static class TradePlanner
    {
        /// <summary>Sells the listed items (each with everything inside it) for <paramref name="buysAt"/> of their value.</summary>
        public static TradePlan PlanSale(ItemCatalog catalog, ItemNode stash, ItemNode character, IReadOnlyList<string> itemIds, double buysAt, Func<string> newId = null)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            if (stash == null)
            {
                throw new ArgumentNullException(nameof(stash));
            }

            if (itemIds == null || itemIds.Count == 0)
            {
                return TradePlan.Failed(TradeErrorCodes.InvalidCount, "Choose something to sell.");
            }

            newId = newId ?? Uuid7.NewId;
            var roots = character != null ? new[] { stash, character } : new[] { stash };
            var parents = new Dictionary<string, ItemNode>(StringComparer.Ordinal);
            var nodes = new Dictionary<string, ItemNode>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                nodes[root.Id] = root;
                foreach (var node in root.Walk())
                {
                    foreach (var child in node.Children)
                    {
                        nodes[child.Id] = child;
                        parents[child.Id] = node;
                    }
                }
            }

            var listed = new HashSet<string>(StringComparer.Ordinal);
            var plan = TradePlan.Started();
            long price = 0;
            foreach (var id in itemIds)
            {
                if (id == null || !nodes.TryGetValue(id, out var item))
                {
                    return TradePlan.Failed(TradeErrorCodes.ItemNotFound, "That item isn't in your stash or gear any more.", id);
                }

                var name = NameOf(catalog, item.DefId);
                if (!parents.ContainsKey(id))
                {
                    return TradePlan.Failed(TradeErrorCodes.NotForSale, "Your stash and character aren't for sale.", id);
                }

                if (!listed.Add(id))
                {
                    return TradePlan.Failed(TradeErrorCodes.ListedTwice, "The " + name + " is listed twice.", id);
                }

                var up = parents[id];
                while (up != null)
                {
                    if (itemIds.Contains(up.Id))
                    {
                        return TradePlan.Failed(TradeErrorCodes.ListedTwice, "The " + name + " is inside the " + NameOf(catalog, up.DefId) + ", which is being sold too.", id);
                    }

                    up = parents.TryGetValue(up.Id, out var above) ? above : null;
                }

                if (item.DefId == WellKnownDefs.Credits)
                {
                    return TradePlan.Failed(TradeErrorCodes.NotForSale, "Money isn't for sale.", id);
                }

                if (item.Walk().Any(n => n.DefId == WellKnownDefs.Credits))
                {
                    return TradePlan.Failed(TradeErrorCodes.MoneyInside, "There's money in the " + name + ". Take it out first.", id);
                }

                long itemPrice = TraderPrices.SellPrice(catalog, item, buysAt);
                if (itemPrice < 1)
                {
                    return TradePlan.Failed(TradeErrorCodes.NotForSale, "The trader doesn't buy the " + name + ".", id);
                }

                price += itemPrice;
            }

            foreach (var id in itemIds)
            {
                var item = nodes[id];
                parents[id].Children.Remove(item);
                plan.Sold.AddRange(item.Walk().Select(n => n.Id));
            }

            var paid = PayIn(catalog, stash, price, plan, newId);
            if (paid != null)
            {
                return paid;
            }

            Check(catalog, roots);
            return plan.Done(price);
        }

        /// <summary>
        /// Buys <paramref name="units"/> (fresh items with new IDs, each with whatever comes inside it) for
        /// <paramref name="price"/> credits in all.
        /// </summary>
        public static TradePlan PlanPurchase(ItemCatalog catalog, ItemNode stash, IReadOnlyList<ItemNode> units, long price)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            if (stash == null)
            {
                throw new ArgumentNullException(nameof(stash));
            }

            if (units == null || units.Count == 0)
            {
                return TradePlan.Failed(TradeErrorCodes.InvalidCount, "Buy at least one.");
            }

            var plan = TradePlan.Started();
            var stacks = MoneyStacks(stash);
            long held = stacks.Sum(s => (long)s.Item.Quantity);
            if (held < price)
            {
                return TradePlan.Failed(TradeErrorCodes.NotEnoughMoney, "That costs " + price + " credits; you have " + held + ".");
            }

            // Smallest stacks first, so paying tidies loose change away.
            long owed = price;
            foreach (var (item, parent) in stacks.OrderBy(s => s.Item.Quantity))
            {
                if (owed <= 0)
                {
                    break;
                }

                if (item.Quantity <= owed)
                {
                    owed -= item.Quantity;
                    parent.Children.Remove(item);
                    plan.Spent.Add(item.Id);
                }
                else
                {
                    item.Quantity -= (int)owed;
                    owed = 0;
                    plan.Updated.Add(item);
                }
            }

            foreach (var unit in units)
            {
                if (!catalog.TryGet(unit.DefId, out var definition))
                {
                    throw new ArgumentException("Unknown item '" + unit.DefId + "' in a purchase.", nameof(units));
                }

                if (!Place(catalog, stash, unit, definition))
                {
                    return TradePlan.Failed(TradeErrorCodes.NoSpace, "There's no room in your stash for the " + NameOf(catalog, unit.DefId) + ".");
                }

                plan.Added.Add(unit);
            }

            Check(catalog, new[] { stash });
            return plan.Done(price);
        }

        /// <summary>Every credit stack in the stash, at any depth, in tree order, with what holds it.</summary>
        public static List<(ItemNode Item, ItemNode Parent)> MoneyStacks(ItemNode stash)
        {
            var stacks = new List<(ItemNode, ItemNode)>();
            if (stash == null)
            {
                return stacks;
            }

            foreach (var node in stash.Walk())
            {
                foreach (var child in node.Children)
                {
                    if (child.DefId == WellKnownDefs.Credits)
                    {
                        stacks.Add((child, node));
                    }
                }
            }

            return stacks;
        }

        /// <summary>How many credits the stash holds.</summary>
        public static long Money(ItemNode stash) => MoneyStacks(stash).Sum(s => (long)s.Item.Quantity);

        // Tops up existing stacks, then starts new ones in the stash's top level. Null when it all fit.
        private static TradePlan PayIn(ItemCatalog catalog, ItemNode stash, long amount, TradePlan plan, Func<string> newId)
        {
            if (!catalog.TryGet(WellKnownDefs.Credits, out var credits))
            {
                throw new InvalidOperationException("The catalog has no '" + WellKnownDefs.Credits + "', so traders can't pay.");
            }

            long left = amount;
            foreach (var (item, _) in MoneyStacks(stash))
            {
                if (left <= 0)
                {
                    break;
                }

                int room = credits.MaxStack - item.Quantity;
                if (room <= 0)
                {
                    continue;
                }

                int add = (int)Math.Min(room, left);
                item.Quantity += add;
                left -= add;
                plan.Updated.Add(item);
            }

            while (left > 0)
            {
                int quantity = (int)Math.Min(credits.MaxStack, left);
                var stack = new ItemNode { Id = newId(), DefId = WellKnownDefs.Credits, Quantity = quantity };
                if (!Place(catalog, stash, stack, credits))
                {
                    return TradePlan.Failed(TradeErrorCodes.NoSpace, "There's no room in your stash for the money.");
                }

                plan.Added.Add(stack);
                left -= quantity;
            }

            return null;
        }

        private static bool Place(ItemCatalog catalog, ItemNode stash, ItemNode item, ItemDefinition definition)
        {
            if (!GridPlacement.TryFindFreeSpot(catalog, stash, definition, out var grid, out var x, out var y, out var rotated))
            {
                return false;
            }

            item.Slot = grid;
            item.X = x;
            item.Y = y;
            item.Rotated = rotated;
            stash.Children.Add(item);
            return true;
        }

        // The result breaking the inventory rules would be a bug here, not a bad request.
        private static void Check(ItemCatalog catalog, IEnumerable<ItemNode> roots)
        {
            foreach (var root in roots)
            {
                var errors = InventoryValidator.Validate(catalog, root);
                if (errors.Count > 0)
                {
                    throw new InvalidOperationException("A trade left " + root.DefId + " invalid: " + errors[0].Code + ": " + errors[0].Message);
                }
            }
        }

        private static string NameOf(ItemCatalog catalog, string defId) =>
            catalog.TryGet(defId, out var definition) && !string.IsNullOrEmpty(definition.DisplayName) ? definition.DisplayName : defId;
    }
}
