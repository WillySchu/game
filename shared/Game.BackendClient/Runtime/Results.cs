using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi
{
    /// <summary>
    /// The answer to a join. On success, spawn the player and equip them from <see cref="Loadout"/>. On
    /// failure, refuse the connection; <see cref="Error"/> says why:
    ///
    /// - invalid_join_token, ticket_expired: the player needs a new ticket from the Hideout.
    /// - wrong_raid: the player's ticket is for a different raid than the one this server is running.
    /// - wrong_map: the player's raid is on a different map from the one this server has loaded
    ///   (BackendClientOptions.MapId). The backend sent them to a misconfigured server.
    /// - raid_ending, raid_aborted: this server's raid is over and takes no one new.
    /// - already_left_raid: this player extracted or died here already.
    /// - backend_unreachable: no usable answer after <see cref="BackendClientOptions.JoinAttempts"/> tries.
    /// - catalog_mismatch, http_401, http_403: this server is misconfigured (built from another catalog, or a
    ///   wrong server token). Every join will fail until it's fixed.
    /// - anything else the backend returns, such as raid_not_found or invalid_request.
    /// </summary>
    public sealed class JoinResult
    {
        private JoinResult(bool succeeded, string playerId, string displayName, ItemNode loadout, bool isReconnect, string error, string message)
        {
            Succeeded = succeeded;
            IsReconnect = isReconnect;
            PlayerId = playerId;
            DisplayName = displayName;
            Loadout = loadout;
            Error = error;
            Message = message;
        }

        public bool Succeeded { get; }

        public string PlayerId { get; }

        public string DisplayName { get; }

        /// <summary>The player's character container and everything on it, as it left their stash.</summary>
        public ItemNode Loadout { get; }

        /// <summary>
        /// This player already joined this raid on this server, and connected again with the same ticket. The
        /// backend answers with the original loadout, but they may already have a character in the world.
        /// </summary>
        public bool IsReconnect { get; }

        public string Error { get; }

        public string Message { get; }

        internal static JoinResult Joined(string playerId, string displayName, ItemNode loadout, bool isReconnect) =>
            new JoinResult(true, playerId, displayName, loadout, isReconnect, null, null);

        internal static JoinResult Failed(string error, string message) =>
            new JoinResult(false, null, null, null, false, error, message);

        public override string ToString() => Succeeded ? $"joined {PlayerId} ({DisplayName})" : $"refused: {Error} {Message}";
    }

    /// <summary>
    /// The backend ended this server's raid on its own, usually because heartbeats didn't reach it for a whole
    /// lease. It has already given everyone still inside their gear back, so nothing more from this raid will
    /// count: send the players back to the Hideout.
    /// </summary>
    public sealed class RaidAbortedEvent
    {
        internal RaidAbortedEvent(string raidId, string message)
        {
            RaidId = raidId;
            Message = message;
        }

        public string RaidId { get; }

        public string Message { get; }
    }

    /// <summary>
    /// The backend refused a report for good. It won't be retried; a copy stays in the outbox directory as
    /// {raid_id}.rejected.jsonl for someone to look at. Usually a bug (an extraction listing an item that
    /// isn't in the raid) or the raid having been aborted first.
    /// </summary>
    public sealed class ReportRejectedEvent
    {
        internal ReportRejectedEvent(string raidId, string playerId, string kind, int statusCode, string error, string message)
        {
            RaidId = raidId;
            PlayerId = playerId;
            Kind = kind;
            StatusCode = statusCode;
            Error = error;
            Message = message;
        }

        public string RaidId { get; }

        /// <summary>Null for the close report.</summary>
        public string PlayerId { get; }

        /// <summary>"extracted", "died" or "close".</summary>
        public string Kind { get; }

        public int StatusCode { get; }

        public string Error { get; }

        public string Message { get; }

        public override string ToString() => $"{Kind} report for raid {RaidId}{(PlayerId != null ? ", player " + PlayerId : "")}: HTTP {StatusCode} {Error} {Message}";
    }
}
