using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RaidZero.Shared.Catalog;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi
{
    /// <summary>
    /// The Hideout's connection to the backend for one logged-in player: their profile, stash edits, deploying
    /// into a raid, and the result afterwards.
    ///
    /// Stash edits are checked locally with the same rules the backend uses before anything is sent, so most
    /// mistakes never cost a round trip. After the backend accepts an edit, the session applies it to its own
    /// copy (using the IDs the backend gave any new stacks) rather than fetching the whole profile again; the
    /// rules are the same on both sides, so both land in the same place. If the stash changed elsewhere in the
    /// meantime (a raid result, another device), the session refreshes it and sends the edit once more if it
    /// still fits.
    ///
    /// Call it from the main thread: it never leaves it, so results and events arrive there too. Calls that
    /// change the profile run one at a time.
    /// </summary>
    public sealed class PlayerSession : IDisposable
    {
        private readonly ItemCatalog _catalog;
        private readonly PlayerSessionOptions _options;
        private readonly IBackendTransport _transport;
        private readonly bool _ownsTransport;
        private readonly SemaphoreSlim _profileLock = new SemaphoreSlim(1, 1);
        private string _token;

        /// <param name="catalog">The same rules the backend runs, for checking edits locally.</param>
        /// <param name="transport">Leave null for HTTP; tests pass their own.</param>
        public PlayerSession(string backendUrl, ItemCatalog catalog, PlayerSessionOptions options = null, IBackendTransport transport = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _options = options ?? new PlayerSessionOptions();
            _ownsTransport = transport == null;
            _transport = transport ?? new HttpClientTransport(backendUrl, _options.RequestTimeout);
        }

        /// <summary>The token expired or was rejected. Send the player back to the login screen.</summary>
        public event Action LoggedOut;

        /// <summary>The profile changed: after a login, a refresh, or an edit. Redraw the stash.</summary>
        public event Action<PlayerProfile> ProfileChanged;

        public bool IsLoggedIn => _token != null;

        /// <summary>The latest profile, or null before the first login.</summary>
        public PlayerProfile Profile { get; private set; }

        /// <summary>
        /// Logs in by display name, creating the player with the starter kit the first time, then loads the
        /// profile. Only works on backends with dev login enabled; Steam login replaces this later.
        /// </summary>
        public async Task<PlayerResult<PlayerProfile>> DevLoginAsync(string displayName, CancellationToken cancellationToken = default)
        {
            var reply = await SendAsync("POST", "/v1/auth/dev-login", new JObject { ["display_name"] = displayName }, authenticated: false, cancellationToken);
            if (!reply.Ok)
            {
                return Fail<PlayerProfile>(reply);
            }

            _token = (string)reply.Json["token"];
            return await RefreshAsync(cancellationToken);
        }

        /// <summary>Forgets the token and profile. Doesn't raise <see cref="LoggedOut"/>; the caller asked for it.</summary>
        public void LogOut()
        {
            _token = null;
            Profile = null;
        }

        /// <summary>Loads the profile again, such as when the Hideout opens or a raid ends.</summary>
        public async Task<PlayerResult<PlayerProfile>> RefreshAsync(CancellationToken cancellationToken = default)
        {
            await _profileLock.WaitAsync(cancellationToken);
            try
            {
                return await RefreshLockedAsync(cancellationToken);
            }
            finally
            {
                _profileLock.Release();
            }
        }

        /// <summary>
        /// Rearranges the stash and gear: move, split, merge or discard, all or nothing. Refused locally when
        /// the rules say no; otherwise sent with the profile version this edit was made against.
        /// </summary>
        public async Task<StashEditResult> ApplyAsync(IReadOnlyList<InventoryOp> ops, CancellationToken cancellationToken = default)
        {
            if (ops == null || ops.Count == 0)
            {
                throw new ArgumentException("An edit needs at least one op.", nameof(ops));
            }

            await _profileLock.WaitAsync(cancellationToken);
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    var retried = attempt > 1;
                    if (_token == null || Profile == null)
                    {
                        return new StashEditResult("not_logged_in", "Log in first.", null, null, retried, null);
                    }

                    // No local "in raid" refusal: a ticket can expire unused, so only the backend knows for sure.
                    var local = new InventoryTree(_catalog, new[] { Profile.Stash.Clone(), Profile.Character.Clone() }, Uuid7.NewId);
                    var check = local.Apply(ops);
                    if (!check.Success)
                    {
                        // On a retry, the stash changed underneath and the edit no longer fits.
                        return new StashEditResult(retried ? "version_conflict" : check.Error.Code, check.Error.Message, check.FailedOpIndex, null, retried, null);
                    }

                    var body = new JObject { ["expected_version"] = Profile.Version, ["ops"] = new JArray(ops.Select(OpToJson)) };
                    var reply = await SendAsync("POST", "/v1/me/inventory/ops", body, authenticated: true, cancellationToken);
                    if (reply.Ok)
                    {
                        var created = ((JArray)reply.Json["created"] ?? new JArray())
                            .Select(c => new CreatedItem((int)c["op_index"], (string)c["item_id"]))
                            .ToList();
                        await ApplyAcceptedLockedAsync(ops, created, (long)reply.Json["profile_version"], cancellationToken);
                        return new StashEditResult(null, null, null, null, retried, created);
                    }

                    if (reply.Error == "version_conflict" && attempt == 1)
                    {
                        var refreshed = await RefreshLockedAsync(cancellationToken);
                        if (!refreshed.Succeeded)
                        {
                            return new StashEditResult(refreshed.Error, refreshed.Message, null, refreshed.RaidId, retried, null);
                        }

                        continue;
                    }

                    // The backend disagreed, or couldn't be reached: whatever happened, show what it has now.
                    if (reply.Error != "logged_out")
                    {
                        await RefreshLockedAsync(cancellationToken);
                    }

                    return new StashEditResult(
                        reply.Error == "invalid_op" ? (string)reply.Json?["reason"] ?? reply.Error : reply.Error,
                        reply.Message,
                        (int?)reply.Json?["op_index"],
                        (string)reply.Json?["raid_id"],
                        retried,
                        null);
                }
            }
            finally
            {
                _profileLock.Release();
            }
        }

        /// <summary>
        /// Reserves a place in a raid on the given map. Connect to the server in the ticket and present its
        /// join token before it expires. From now until the raid resolves, the stash is locked.
        /// </summary>
        public async Task<PlayerResult<RaidTicketInfo>> RequestTicketAsync(string mapId, CancellationToken cancellationToken = default)
        {
            var reply = await SendAsync("POST", "/v1/me/raid-tickets", new JObject { ["map_id"] = mapId }, authenticated: true, cancellationToken);
            if (!reply.Ok)
            {
                return Fail<RaidTicketInfo>(reply);
            }

            var json = reply.Json;
            var ticket = new RaidTicketInfo(
                (string)json["raid_id"],
                (string)json["join_token"],
                (string)json["server"]?["host"],
                (int?)json["server"]?["port"] ?? 0,
                (DateTimeOffset?)json["expires_at"] ?? DateTimeOffset.UtcNow);
            await RefreshAsync(cancellationToken);
            return new PlayerResult<RaidTicketInfo>(ticket, null, null, null);
        }

        /// <summary>What a raid did to the player's gear so far. Check <see cref="RaidOutcome.IsFinished"/>.</summary>
        public async Task<PlayerResult<RaidOutcome>> GetRaidResultAsync(string raidId, CancellationToken cancellationToken = default)
        {
            if (!Uuid7.IsValid(raidId))
            {
                return new PlayerResult<RaidOutcome>(null, "raid_not_found", "That isn't a raid ID.", null);
            }

            var reply = await SendAsync("GET", "/v1/me/raids/" + raidId, null, authenticated: true, cancellationToken);
            if (!reply.Ok)
            {
                return Fail<RaidOutcome>(reply);
            }

            var json = reply.Json;
            List<string> Ids(string name) => ((JArray)json[name] ?? new JArray()).Select(t => (string)t).ToList();
            var outcome = new RaidOutcome((string)json["raid_id"], (string)json["map_id"], (string)json["raid_state"], (string)json["outcome"],
                Ids("items_gained"), Ids("items_returned"), Ids("items_lost"));
            return new PlayerResult<RaidOutcome>(outcome, null, null, null);
        }

        /// <summary>
        /// Back from a raid: waits until its result is final, then refreshes the profile. The game server
        /// reports outcomes in the background, so the result can take a moment to land after the player
        /// leaves. Fails with timed_out if it takes longer than <paramref name="timeout"/>.
        /// </summary>
        public async Task<PlayerResult<RaidOutcome>> WaitForRaidResultAsync(string raidId, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var result = await GetRaidResultAsync(raidId, cancellationToken);
                if (!result.Succeeded && result.Error != "backend_unreachable")
                {
                    return result;
                }

                if (result.Succeeded && result.Value.IsFinished)
                {
                    await RefreshAsync(cancellationToken);
                    return result;
                }

                if (DateTime.UtcNow + _options.ResultPollInterval > deadline)
                {
                    return new PlayerResult<RaidOutcome>(result.Value, "timed_out", "The raid's result hasn't arrived yet.", raidId);
                }

                await Task.Delay(_options.ResultPollInterval, cancellationToken);
            }
        }

        public void Dispose()
        {
            if (_ownsTransport)
            {
                _transport.Dispose();
            }
        }

        // ---- Internals ----

        private async Task<PlayerResult<PlayerProfile>> RefreshLockedAsync(CancellationToken cancellationToken)
        {
            var reply = await SendAsync("GET", "/v1/me/profile", null, authenticated: true, cancellationToken);
            if (!reply.Ok)
            {
                return Fail<PlayerProfile>(reply);
            }

            var json = reply.Json;
            var profile = new PlayerProfile(
                (string)json["player_id"],
                (string)json["display_name"],
                (long)json["profile_version"],
                Wire.ToItemNode(json["stash"]),
                Wire.ToItemNode(json["character"]),
                (string)json["active_raid_id"],
                (string)json["last_raid_id"]);
            SetProfile(profile);
            return new PlayerResult<PlayerProfile>(profile, null, null, null);
        }

        /// <summary>
        /// The backend accepted the edit: make the same change to our copy. Each split mints exactly one ID, so
        /// the k-th ID asked for belongs to the k-th split, and the backend told us which ID that got. A split
        /// whose stack was merged away later in the same edit isn't reported, but then its ID never shows.
        /// </summary>
        private async Task ApplyAcceptedLockedAsync(IReadOnlyList<InventoryOp> ops, List<CreatedItem> created, long version, CancellationToken cancellationToken)
        {
            var byOp = created.ToDictionary(c => c.OpIndex, c => c.ItemId);
            var splits = new Queue<int>(Enumerable.Range(0, ops.Count).Where(i => ops[i].Op == InventoryOpKinds.Split));
            string NextId() => splits.Count > 0 && byOp.TryGetValue(splits.Dequeue(), out var id) ? id : Uuid7.NewId();

            var tree = new InventoryTree(_catalog, new[] { Profile.Stash.Clone(), Profile.Character.Clone() }, NextId);
            if (!tree.Apply(ops).Success)
            {
                // Can't happen while both sides run the same rules; if it does, trust the backend.
                await RefreshLockedAsync(cancellationToken);
                return;
            }

            var stash = tree.Roots.First(r => r.Id == Profile.Stash.Id);
            var character = tree.Roots.First(r => r.Id == Profile.Character.Id);
            SetProfile(new PlayerProfile(Profile.PlayerId, Profile.DisplayName, version, stash, character, Profile.ActiveRaidId, Profile.LastRaidId));
        }

        private void SetProfile(PlayerProfile profile)
        {
            Profile = profile;
            ProfileChanged?.Invoke(profile);
        }

        private static JObject OpToJson(InventoryOp op)
        {
            var json = new JObject { ["op"] = op.Op, ["item_id"] = op.ItemId };
            if (op.To != null)
            {
                var to = new JObject { ["parent_id"] = op.To.ParentId, ["slot"] = op.To.Slot };
                if (op.To.X.HasValue)
                {
                    to["x"] = op.To.X.Value;
                }

                if (op.To.Y.HasValue)
                {
                    to["y"] = op.To.Y.Value;
                }

                if (op.To.Rotated)
                {
                    to["rotated"] = true;
                }

                json["to"] = to;
            }

            if (op.IntoId != null)
            {
                json["into_id"] = op.IntoId;
            }

            if (op.Quantity != 0)
            {
                json["quantity"] = op.Quantity;
            }

            return json;
        }

        private static PlayerResult<T> Fail<T>(Reply reply) => new PlayerResult<T>(default, reply.Error, reply.Message, (string)reply.Json?["raid_id"]);

        private async Task<Reply> SendAsync(string method, string path, JObject body, bool authenticated, CancellationToken cancellationToken)
        {
            if (authenticated && _token == null)
            {
                return new Reply(0, null, "not_logged_in", "Log in first.");
            }

            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (authenticated)
            {
                headers["Authorization"] = "Bearer " + _token;
            }

            BackendResponse response;
            try
            {
                response = await _transport.SendAsync(new BackendRequest(method, path, body == null ? null : Wire.Compact(body), headers), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new Reply(0, null, "backend_unreachable", exception.Message);
            }

            var json = Wire.TryParseObject(response.Body);
            if (response.StatusCode >= 200 && response.StatusCode < 300)
            {
                return json != null
                    ? new Reply(response.StatusCode, json, null, null)
                    : new Reply(response.StatusCode, null, "invalid_response", "The backend's answer couldn't be read.");
            }

            if (response.StatusCode == 401 && authenticated)
            {
                _token = null;
                LoggedOut?.Invoke();
                return new Reply(401, json, "logged_out", "Your session expired. Log in again.");
            }

            return new Reply(response.StatusCode, json, (string)json?["error"] ?? "http_" + response.StatusCode, (string)json?["message"] ?? "");
        }

        private sealed class Reply
        {
            public Reply(int status, JObject json, string error, string message)
            {
                Status = status;
                Json = json;
                Error = error;
                Message = message;
            }

            public int Status { get; }

            public JObject Json { get; }

            public string Error { get; }

            public string Message { get; }

            public bool Ok => Error == null;
        }
    }
}
