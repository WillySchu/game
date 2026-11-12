using System;
using System.Collections.Generic;
using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi
{
    /// <summary>The player's stash, gear and raid status, as the backend last reported them.</summary>
    public sealed class PlayerProfile
    {
        public PlayerProfile(string playerId, string displayName, long version, ItemNode stash, ItemNode character, string activeRaidId, string lastRaidId)
        {
            PlayerId = playerId;
            DisplayName = displayName;
            Version = version;
            Stash = stash;
            Character = character;
            ActiveRaidId = activeRaidId;
            LastRaidId = lastRaidId;
        }

        public string PlayerId { get; }

        public string DisplayName { get; }

        /// <summary>Bumped by every change to the stash or gear. Stash edits are checked against it.</summary>
        public long Version { get; }

        /// <summary>The stash container and everything in it. Don't modify it; the session replaces it on every change.</summary>
        public ItemNode Stash { get; }

        /// <summary>The character container: what the player will take into the next raid.</summary>
        public ItemNode Character { get; }

        /// <summary>
        /// Set while the player holds a ticket or is in a raid. Their stash and gear are locked until it clears:
        /// show "in raid", and wait for the result.
        /// </summary>
        public string ActiveRaidId { get; }

        /// <summary>The most recent raid they joined, finished or not. Remember which results you've shown.</summary>
        public string LastRaidId { get; }

        public bool IsInRaid => ActiveRaidId != null;
    }

    /// <summary>A place in a raid: which server to connect to, and the token to present when connecting.</summary>
    public sealed class RaidTicketInfo
    {
        public RaidTicketInfo(string raidId, string joinToken, string serverHost, int serverPort, DateTimeOffset expiresAt)
        {
            RaidId = raidId;
            JoinToken = joinToken;
            ServerHost = serverHost;
            ServerPort = serverPort;
            ExpiresAt = expiresAt;
        }

        public string RaidId { get; }

        public string JoinToken { get; }

        public string ServerHost { get; }

        public int ServerPort { get; }

        /// <summary>Connect before this, or the server refuses the token and the stash unlocks again.</summary>
        public DateTimeOffset ExpiresAt { get; }
    }

    /// <summary>A trader, and what they sell to this player.</summary>
    public sealed class TraderInfo
    {
        public TraderInfo(string traderId, string displayName, double buysAt, IReadOnlyList<TraderOfferInfo> offers)
        {
            TraderId = traderId;
            DisplayName = displayName;
            BuysAt = buysAt;
            Offers = offers ?? new TraderOfferInfo[0];
        }

        public string TraderId { get; }

        public string DisplayName { get; }

        /// <summary>The share of an item's value they pay for it: give it to TraderPrices.SellPrice to show an offer.</summary>
        public double BuysAt { get; }

        public IReadOnlyList<TraderOfferInfo> Offers { get; }
    }

    public sealed class TraderOfferInfo
    {
        public TraderOfferInfo(string offerId, long price, ItemNode item, int? stock, int? left, DateTimeOffset? restocksAt)
        {
            OfferId = offerId;
            Price = price;
            Item = item;
            Stock = stock;
            Left = left;
            RestocksAt = restocksAt;
        }

        public string OfferId { get; }

        /// <summary>Credits for one.</summary>
        public long Price { get; }

        /// <summary>What one purchase gives, with whatever comes in it. The items have no IDs: it's a description.</summary>
        public ItemNode Item { get; }

        /// <summary>How many per restock; null for no limit.</summary>
        public int? Stock { get; }

        /// <summary>How many this player can still buy before it restocks; null for no limit.</summary>
        public int? Left { get; }

        /// <summary>When more come in; null for no limit.</summary>
        public DateTimeOffset? RestocksAt { get; }
    }

    /// <summary>How a purchase or sale went. Either way, the session's profile is now the backend's current one.</summary>
    public sealed class TradeOutcome
    {
        internal TradeOutcome(string error, string message, string itemId, string raidId, long credits, IReadOnlyList<string> itemIds)
        {
            Error = error;
            Message = message;
            ItemId = itemId;
            RaidId = raidId;
            Credits = credits;
            ItemIds = itemIds ?? new string[0];
        }

        public bool Succeeded => Error == null;

        /// <summary>
        /// Why it was refused: a trade reason (TradeErrorCodes, such as not_enough_money or out_of_stock), in_raid,
        /// version_conflict (the stash changed underneath twice running), unknown_trader, or one of the session's own codes.
        /// </summary>
        public string Error { get; }

        /// <summary>A sentence to show the player.</summary>
        public string Message { get; }

        /// <summary>The item a refusal is about, when there is one.</summary>
        public string ItemId { get; }

        /// <summary>For in_raid: the raid holding the stash.</summary>
        public string RaidId { get; }

        /// <summary>Credits spent on a purchase, or paid for a sale.</summary>
        public long Credits { get; }

        /// <summary>A purchase: the items bought. A sale: the items sold, with everything that was in them.</summary>
        public IReadOnlyList<string> ItemIds { get; }

        public override string ToString() => Succeeded ? "ok, " + Credits + " credits" : Error + ": " + Message;
    }

    /// <summary>What a raid did to the player's gear, for the post-raid screen.</summary>
    public sealed class RaidOutcome
    {
        public RaidOutcome(string raidId, string mapId, string raidState, string outcome, IReadOnlyList<string> gained, IReadOnlyList<string> returned, IReadOnlyList<string> lost)
        {
            RaidId = raidId;
            MapId = mapId;
            RaidState = raidState;
            Outcome = outcome;
            ItemsGained = gained;
            ItemsReturned = returned;
            ItemsLost = lost;
        }

        public string RaidId { get; }

        public string MapId { get; }

        /// <summary>pending, active, closed or aborted.</summary>
        public string RaidState { get; }

        /// <summary>
        /// The player's part in it: ticketed and in_raid while it's still going, then extracted, died, refunded
        /// (the raid was aborted and their gear given back) or expired (they never connected).
        /// </summary>
        public string Outcome { get; }

        /// <summary>Whether the outcome is final, so the post-raid screen can show it.</summary>
        public bool IsFinished => Outcome == "extracted" || Outcome == "died" || Outcome == "refunded" || Outcome == "expired";

        /// <summary>New loot they brought home.</summary>
        public IReadOnlyList<string> ItemsGained { get; }

        /// <summary>Their own gear that came home.</summary>
        public IReadOnlyList<string> ItemsReturned { get; }

        /// <summary>Their own gear that didn't.</summary>
        public IReadOnlyList<string> ItemsLost { get; }
    }

    /// <summary>
    /// How a call went. On failure, <see cref="Error"/> is the backend's error code, such as already_in_raid or
    /// no_server_available, or one of the session's own: backend_unreachable, logged_out, not_logged_in.
    /// </summary>
    public sealed class PlayerResult<T>
    {
        internal PlayerResult(T value, string error, string message, string raidId)
        {
            Value = value;
            Error = error;
            Message = message;
            RaidId = raidId;
        }

        public bool Succeeded => Error == null;

        public T Value { get; }

        public string Error { get; }

        public string Message { get; }

        /// <summary>For in_raid and already_in_raid: the raid the player is committed to.</summary>
        public string RaidId { get; }

        public override string ToString() => Succeeded ? "ok" : Error + ": " + Message;
    }

    /// <summary>How a stash edit went. Either way, the session's profile is now the backend's current one.</summary>
    public sealed class StashEditResult
    {
        internal StashEditResult(string error, string message, int? failedOpIndex, string raidId, bool retried, IReadOnlyList<CreatedItem> created)
        {
            Error = error;
            Message = message;
            FailedOpIndex = failedOpIndex;
            RaidId = raidId;
            Retried = retried;
            Created = created ?? new CreatedItem[0];
        }

        public bool Succeeded => Error == null;

        /// <summary>
        /// Why it was refused. An inventory reason such as slot_occupied (checked locally first, so most never
        /// reach the backend), version_conflict (the stash changed underneath and the edit no longer fits),
        /// in_raid, or one of the session's own codes.
        /// </summary>
        public string Error { get; }

        public string Message { get; }

        public int? FailedOpIndex { get; }

        /// <summary>For in_raid: the raid holding the stash.</summary>
        public string RaidId { get; }

        /// <summary>The stash had changed elsewhere; the session refreshed it and sent the edit again.</summary>
        public bool Retried { get; }

        /// <summary>Stacks that splits created, by op index.</summary>
        public IReadOnlyList<CreatedItem> Created { get; }

        public override string ToString() => Succeeded ? "ok" : Error + (FailedOpIndex.HasValue ? " (op " + FailedOpIndex + ")" : "") + ": " + Message;
    }

    public sealed class PlayerSessionOptions
    {
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>How often <see cref="PlayerSession.WaitForRaidResultAsync"/> asks whether the result is in.</summary>
        public TimeSpan ResultPollInterval { get; set; } = TimeSpan.FromSeconds(2);
    }
}
