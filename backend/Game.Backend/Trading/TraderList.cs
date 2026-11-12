using System.Text.Json;
using RaidZero.Backend.Players;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Trading;

namespace RaidZero.Backend.Trading;

/// <summary>One thing a trader sells: an item (with whatever comes in it), its price, and how many per restock.</summary>
public sealed class TraderOffer
{
    public string OfferId { get; set; } = "";

    /// <summary>Credits for one.</summary>
    public long Price { get; set; }

    /// <summary>How many each player can buy per restock; null for no limit.</summary>
    public int? Stock { get; set; }

    /// <summary>How often stock comes back, on the clock (every 240 minutes is at 00:00, 04:00, ... UTC).</summary>
    public int? RestockMinutes { get; set; }

    /// <summary>What one purchase gives: an item spec, as in starter_kit.json, without a slot or position.</summary>
    public ItemNode Item { get; set; } = new();
}

public sealed class Trader
{
    public string TraderId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>The share of an item's value the trader pays for it, above 0 and at most 1.</summary>
    public double BuysAt { get; set; }

    public List<TraderOffer> Offers { get; set; } = [];

    public TraderOffer? FindOffer(string? offerId) => Offers.FirstOrDefault(o => o.OfferId == offerId);
}

/// <summary>
/// The traders, read from traders.json and checked against the catalog at startup, like the starter kit: if
/// an item rule changes and an offer no longer makes sense, the backend refuses to start rather than selling
/// something that fails later.
/// </summary>
public sealed class TraderList
{
    /// <summary>The most of one offer a single purchase can buy.</summary>
    public const int MaxCount = 20;

    private TraderList(List<Trader> traders)
    {
        All = traders;
    }

    public IReadOnlyList<Trader> All { get; }

    public Trader? Find(string? traderId) => All.FirstOrDefault(t => t.TraderId == traderId);

    public static TraderList LoadFile(string path, ItemCatalog catalog)
    {
        if (!File.Exists(path))
        {
            throw new TraderListException($"Traders file not found at '{Path.GetFullPath(path)}'.");
        }

        return Parse(File.ReadAllText(path), catalog, path);
    }

    public static TraderList Parse(string json, ItemCatalog catalog, string source = "traders.json")
    {
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(json, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            throw new TraderListException($"{source} is not valid JSON: {ex.Message}", ex);
        }

        var traders = document?.Traders ?? [];
        var problems = new List<string>();
        var traderIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trader in traders)
        {
            var who = string.IsNullOrEmpty(trader.TraderId) ? "a trader" : trader.TraderId;
            if (!IsIdentifier(trader.TraderId))
            {
                problems.Add($"{who}: trader_id must be lowercase letters, digits and underscores.");
            }
            else if (!traderIds.Add(trader.TraderId))
            {
                problems.Add($"{who}: listed twice.");
            }

            if (string.IsNullOrWhiteSpace(trader.DisplayName))
            {
                trader.DisplayName = trader.TraderId;
            }

            if (!(trader.BuysAt > 0 && trader.BuysAt <= 1))
            {
                problems.Add($"{who}: buys_at must be above 0 and at most 1.");
            }

            var offerIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var offer in trader.Offers ?? [])
            {
                var what = $"{who}, offer {(string.IsNullOrEmpty(offer.OfferId) ? "?" : offer.OfferId)}";
                if (!IsIdentifier(offer.OfferId))
                {
                    problems.Add($"{what}: offer_id must be lowercase letters, digits and underscores.");
                }
                else if (!offerIds.Add(offer.OfferId))
                {
                    problems.Add($"{what}: listed twice.");
                }

                if (offer.Price < 1)
                {
                    problems.Add($"{what}: price must be at least 1.");
                }

                if (offer.Stock is < 1)
                {
                    problems.Add($"{what}: stock must be at least 1, or left out for no limit.");
                }

                if (offer.Stock is not null && offer.RestockMinutes is null or < 1)
                {
                    problems.Add($"{what}: an offer with stock needs restock_minutes of at least 1.");
                }

                if (offer.Item?.DefId == WellKnownDefs.Credits)
                {
                    problems.Add($"{what}: traders don't sell money.");
                    continue;
                }

                var problem = CheckItem(catalog, offer.Item);
                if (problem is not null)
                {
                    problems.Add($"{what}: {problem}");
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new TraderListException($"{source} doesn't fit the item catalog:\n  " + string.Join("\n  ", problems));
        }

        return new TraderList(traders);
    }

    /// <summary>One purchase of the offer: its item with new IDs, not yet placed anywhere.</summary>
    public static ItemNode Instantiate(ItemCatalog catalog, TraderOffer offer, Func<string>? newId = null)
    {
        var item = ItemSpecs.Build(catalog, offer.Item, newId ?? Uuid7.NewId);
        (item.Slot, item.X, item.Y, item.Rotated) = (null, null, null, false);
        return item;
    }

    // Builds the item and buys it into an empty stash: that checks it's real, fits together and fits a stash.
    private static string? CheckItem(ItemCatalog catalog, ItemNode? spec)
    {
        if (spec is null || string.IsNullOrEmpty(spec.DefId))
        {
            return "needs an item.";
        }

        if (!catalog.TryGet(WellKnownDefs.Stash, out _))
        {
            return "the catalog has no stash to check it against.";
        }

        try
        {
            var offer = new TraderOffer { Item = spec };
            var item = Instantiate(catalog, offer);
            var stash = new ItemNode { Id = Uuid7.NewId(), DefId = WellKnownDefs.Stash };
            stash.Children.Add(new ItemNode { Id = Uuid7.NewId(), DefId = WellKnownDefs.Credits, Slot = SlotNames.Grid, X = 0, Y = 0, Quantity = 1 });
            var plan = TradePlanner.PlanPurchase(catalog, stash, [item], 1);
            return plan.Success ? null : plan.Message;
        }
        catch (ItemSpecException ex)
        {
            return ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            // The planner refuses to leave an invalid stash: the item breaks a rule (a wrong slot, too many rounds).
            return ex.Message;
        }
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    private sealed class Document
    {
        public List<Trader>? Traders { get; set; }
    }
}

public sealed class TraderListException : Exception
{
    public TraderListException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
