using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using RaidZero.Backend.Data;
using RaidZero.Backend.Raids;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Trading;

namespace RaidZero.Backend.Trading;

public enum TradeStatus
{
    Done,
    PlayerNotFound,
    UnknownTrader,
    VersionConflict,
    Rejected,

    /// <summary>The player holds a raid ticket or is in a raid; their stash is locked until it resolves.</summary>
    InRaid,
}

/// <summary>How a purchase or sale went.</summary>
public sealed class TradeResult
{
    public TradeStatus Status { get; private init; }

    /// <summary>The version after the trade; the current version on a conflict or refusal.</summary>
    public long ProfileVersion { get; private init; }

    /// <summary>Credits spent on a purchase, or paid for a sale.</summary>
    public long Credits { get; private init; }

    /// <summary>A purchase: the items bought. A sale: the items sold, with everything that was in them.</summary>
    public IReadOnlyList<string> ItemIds { get; private init; } = [];

    /// <summary>Why it was refused: one of <see cref="TradeErrorCodes"/>.</summary>
    public string? Error { get; private init; }

    public string? Message { get; private init; }

    /// <summary>The item a refusal is about, when there is one.</summary>
    public string? ItemId { get; private init; }

    /// <summary>The raid holding the player, when <see cref="Status"/> is InRaid.</summary>
    public string? RaidId { get; private init; }

    internal static TradeResult Ok(long version, long credits, IReadOnlyList<string> items) =>
        new() { Status = TradeStatus.Done, ProfileVersion = version, Credits = credits, ItemIds = items };

    internal static TradeResult Of(TradeStatus status) => new() { Status = status };

    internal static TradeResult Locked(string raidId) => new() { Status = TradeStatus.InRaid, RaidId = raidId };

    internal static TradeResult Conflict(long current) => new() { Status = TradeStatus.VersionConflict, ProfileVersion = current };

    internal static TradeResult Refused(long current, string error, string message, string? itemId = null) =>
        new() { Status = TradeStatus.Rejected, ProfileVersion = current, Error = error, Message = message, ItemId = itemId };
}

/// <summary>An offer as one player sees it: how many are left for them, and when more come.</summary>
public sealed record OfferView(TraderOffer Offer, int? Left, DateTimeOffset? RestocksAt);

/// <summary>
/// Buying from and selling to traders: the backend half of /v1/me/traders. Like stash edits, each trade is
/// one write transaction that checks the stash isn't locked by a raid and the profile version is the one the
/// client saw, works out the change with the shared <see cref="TradePlanner"/>, writes it, bumps the version
/// and records a ledger entry ('purchase' or 'sell'). Sending the same trade twice can't do it twice: the
/// second carries a version that's now stale.
/// </summary>
public sealed class TraderService
{
    private readonly Database _db;
    private readonly ItemCatalog _catalog;
    private readonly TraderList _traders;
    private readonly TimeProvider _time;

    public TraderService(Database db, ItemCatalog catalog, TraderList traders, TimeProvider time)
    {
        _db = db;
        _catalog = catalog;
        _traders = traders;
        _time = time;
    }

    public IReadOnlyList<Trader> Traders => _traders.All;

    /// <summary>Every offer of a trader, with what's left of it for this player.</summary>
    public Task<IReadOnlyList<OfferView>> OffersAsync(string playerId, Trader trader, CancellationToken ct = default) =>
        _db.ReadAsync<IReadOnlyList<OfferView>>(async c =>
        {
            var bought = (await c.QueryAsync<StockRow>(
                    "SELECT offer_id AS OfferId, period AS Period, bought AS Bought FROM trader_purchases WHERE player_id = @playerId AND trader_id = @traderId",
                    new { playerId, traderId = trader.TraderId }))
                .ToDictionary(r => r.OfferId, StringComparer.Ordinal);
            var now = _time.GetUtcNow();
            return trader.Offers.Select(offer =>
            {
                if (offer.Stock is null)
                {
                    return new OfferView(offer, null, null);
                }

                long period = Period(offer, now);
                long used = bought.TryGetValue(offer.OfferId, out var row) && row.Period == period ? row.Bought : 0;
                return new OfferView(offer, (int)Math.Max(0, offer.Stock.Value - used), PeriodStart(offer, period + 1));
            }).ToList();
        }, ct);

    public Task<TradeResult> BuyAsync(string playerId, long expectedVersion, string traderId, string offerId, int count, CancellationToken ct = default)
    {
        var trader = _traders.Find(traderId);
        if (trader is null)
        {
            return Task.FromResult(TradeResult.Of(TradeStatus.UnknownTrader));
        }

        return _db.WriteAsync(async (c, tx) =>
        {
            var start = await StartAsync(c, tx, playerId, expectedVersion);
            if (start.Result is not null)
            {
                return start.Result;
            }

            var version = start.Player!.ProfileVersion;
            var offer = trader.FindOffer(offerId);
            if (offer is null)
            {
                return TradeResult.Refused(version, TradeErrorCodes.UnknownOffer, $"{trader.DisplayName} doesn't sell that.");
            }

            if (count < 1 || count > TraderList.MaxCount)
            {
                return TradeResult.Refused(version, TradeErrorCodes.InvalidCount, $"Buy from 1 to {TraderList.MaxCount} at a time.");
            }

            var now = _time.GetUtcNow();
            long period = 0, alreadyBought = 0;
            if (offer.Stock is not null)
            {
                period = Period(offer, now);
                var row = (await c.QueryAsync<StockRow>(
                    "SELECT offer_id AS OfferId, period AS Period, bought AS Bought FROM trader_purchases WHERE player_id = @playerId AND trader_id = @traderId AND offer_id = @offerId",
                    new { playerId, traderId, offerId }, transaction: tx)).FirstOrDefault();
                alreadyBought = row is not null && row.Period == period ? row.Bought : 0;
                long left = offer.Stock.Value - alreadyBought;
                if (count > left)
                {
                    var wait = Until(PeriodStart(offer, period + 1) - now);
                    return TradeResult.Refused(version, TradeErrorCodes.OutOfStock, left <= 0
                        ? $"{trader.DisplayName} is out of those. More in {wait}."
                        : $"{trader.DisplayName} has only {left} left. More in {wait}.");
                }
            }

            var items = await ItemRows.LoadStashItemsAsync(c, tx, playerId);
            var stash = items[start.Player.StashRootId];
            var units = Enumerable.Range(0, count).Select(_ => TraderList.Instantiate(_catalog, offer)).ToList();
            var plan = TradePlanner.PlanPurchase(_catalog, stash, units, offer.Price * count);
            if (!plan.Success)
            {
                return TradeResult.Refused(version, plan.Error!, plan.Message!, plan.ItemId);
            }

            var stamp = Timestamps.Format(now);
            await WriteAsync(c, tx, playerId, stash.Id, plan, removedState: ItemStates.Consumed, stamp);
            if (offer.Stock is not null)
            {
                await c.ExecuteAsync(
                    """
                    INSERT INTO trader_purchases (player_id, trader_id, offer_id, period, bought)
                    VALUES (@playerId, @traderId, @offerId, @period, @bought)
                    ON CONFLICT (player_id, trader_id, offer_id) DO UPDATE SET period = excluded.period, bought = excluded.bought
                    """,
                    new { playerId, traderId, offerId, period, bought = alreadyBought + count }, transaction: tx);
            }

            var bought = plan.Added.Select(a => a.Id).ToList();
            var newVersion = await BumpAsync(c, tx, playerId, version);
            await LedgerAsync(c, tx, playerId, "purchase", new
            {
                ProfileVersion = newVersion,
                TraderId = traderId,
                OfferId = offerId,
                Count = count,
                Credits = plan.Credits,
                Created = plan.Added.SelectMany(a => a.Walk()).Select(n => n.Id),
                Spent = plan.Spent,
                Updated = plan.Updated.Select(n => n.Id),
            }, stamp);
            return TradeResult.Ok(newVersion, plan.Credits, bought);
        }, ct);
    }

    public Task<TradeResult> SellAsync(string playerId, long expectedVersion, string traderId, IReadOnlyList<string> itemIds, CancellationToken ct = default)
    {
        var trader = _traders.Find(traderId);
        if (trader is null)
        {
            return Task.FromResult(TradeResult.Of(TradeStatus.UnknownTrader));
        }

        return _db.WriteAsync(async (c, tx) =>
        {
            var start = await StartAsync(c, tx, playerId, expectedVersion);
            if (start.Result is not null)
            {
                return start.Result;
            }

            var version = start.Player!.ProfileVersion;
            var items = await ItemRows.LoadStashItemsAsync(c, tx, playerId);
            var stash = items[start.Player.StashRootId];
            var plan = TradePlanner.PlanSale(_catalog, stash, items[start.Player.CharacterRootId], itemIds, trader.BuysAt);
            if (!plan.Success)
            {
                return TradeResult.Refused(version, plan.Error!, plan.Message!, plan.ItemId);
            }

            var stamp = Timestamps.Format(_time.GetUtcNow());
            await WriteAsync(c, tx, playerId, stash.Id, plan, removedState: ItemStates.Sold, stamp);
            var newVersion = await BumpAsync(c, tx, playerId, version);
            await LedgerAsync(c, tx, playerId, "sell", new
            {
                ProfileVersion = newVersion,
                TraderId = traderId,
                Credits = plan.Credits,
                Sold = plan.Sold,
                Created = plan.Added.Select(a => a.Id),
                Updated = plan.Updated.Select(n => n.Id),
            }, stamp);
            return TradeResult.Ok(newVersion, plan.Credits, plan.Sold);
        }, ct);
    }

    /// <summary>The restock period an offer is in at a time: whole minutes since 1970, divided by its restock time.</summary>
    public static long Period(TraderOffer offer, DateTimeOffset time) =>
        (long)Math.Floor(time.ToUnixTimeSeconds() / 60.0) / Math.Max(1, offer.RestockMinutes ?? 1);

    public static DateTimeOffset PeriodStart(TraderOffer offer, long period) =>
        DateTimeOffset.FromUnixTimeSeconds(period * Math.Max(1, offer.RestockMinutes ?? 1) * 60L);

    /// <summary>"2 h 5 min", "40 min", or "1 min" for anything shorter.</summary>
    public static string Until(TimeSpan wait)
    {
        var minutes = Math.Max(1, (long)Math.Ceiling(wait.TotalMinutes));
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }

    // The checks every trade starts with. Result is set when the trade stops here.
    private async Task<(PlayerRow? Player, TradeResult? Result)> StartAsync(SqliteConnection c, SqliteTransaction tx, string playerId, long expectedVersion)
    {
        var player = (await c.QueryAsync<PlayerRow>(
            "SELECT profile_version AS ProfileVersion, stash_root_id AS StashRootId, character_root_id AS CharacterRootId FROM players WHERE id = @playerId",
            new { playerId }, transaction: tx)).FirstOrDefault();
        if (player is null)
        {
            return (null, TradeResult.Of(TradeStatus.PlayerNotFound));
        }

        // Gear in a raid is the game server's; a refund or extraction must not land in a stash changed since.
        var raidId = await RaidService.LiveRaidIdAsync(c, tx, playerId, Timestamps.Format(_time.GetUtcNow()));
        if (raidId is not null)
        {
            return (player, TradeResult.Locked(raidId));
        }

        return player.ProfileVersion != expectedVersion
            ? (player, TradeResult.Conflict(player.ProfileVersion))
            : (player, null);
    }

    // Removed items first (so they stop counting for the one-item-per-slot index), then the money stacks that
    // changed, then the new items.
    private static async Task WriteAsync(SqliteConnection c, SqliteTransaction tx, string playerId, string stashId, TradePlan plan, string removedState, string now)
    {
        foreach (var id in plan.Sold.Concat(plan.Spent))
        {
            var changed = await c.ExecuteAsync(
                "UPDATE item_instances SET state = @removedState, updated_at = @now WHERE id = @id AND owner_id = @playerId AND state = 'stash'",
                new { removedState, now, id, playerId }, transaction: tx);
            if (changed != 1)
            {
                throw new InvalidOperationException($"Item {id} was not in {playerId}'s stash when the trade was written.");
            }
        }

        foreach (var stack in plan.Updated)
        {
            await c.ExecuteAsync(
                "UPDATE item_instances SET quantity = @quantity, updated_at = @now WHERE id = @id AND owner_id = @playerId AND state = 'stash'",
                new { quantity = (long)stack.Quantity, now, id = stack.Id, playerId }, transaction: tx);
        }

        // From a trader: what was bought, and the money paid for a sale.
        foreach (var item in plan.Added)
        {
            await ItemRows.InsertTreeAsync(c, tx, item, stashId, playerId, ItemStates.Stash, "purchase", now);
        }
    }

    private static async Task<long> BumpAsync(SqliteConnection c, SqliteTransaction tx, string playerId, long version)
    {
        var newVersion = version + 1;
        var bumped = await c.ExecuteAsync(
            "UPDATE players SET profile_version = @newVersion WHERE id = @playerId AND profile_version = @version",
            new { newVersion, playerId, version }, transaction: tx);
        if (bumped != 1)
        {
            throw new InvalidOperationException($"Profile version of {playerId} changed mid-transaction.");
        }

        return newVersion;
    }

    private static Task LedgerAsync(SqliteConnection c, SqliteTransaction tx, string playerId, string kind, object detail, string now) =>
        c.ExecuteAsync(
            "INSERT INTO ledger_entries (player_id, kind, detail, created_at) VALUES (@playerId, @kind, @detail, @now)",
            new { playerId, kind, detail = JsonSerializer.Serialize(detail, JsonDefaults.Options), now }, transaction: tx);

    private sealed class PlayerRow
    {
        public long ProfileVersion { get; set; }

        public string StashRootId { get; set; } = "";

        public string CharacterRootId { get; set; } = "";
    }

    private sealed class StockRow
    {
        public string OfferId { get; set; } = "";

        public long Period { get; set; }

        public long Bought { get; set; }
    }
}
