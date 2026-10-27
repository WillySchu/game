using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.BackendApi
{
    /// <summary>
    /// The game server's connection to the backend for one raid: letting players in, keeping the raid's lease
    /// alive, and reporting how each player left.
    ///
    /// Typical use, from the server's main thread:
    ///   var backend = new BackendClient(BackendSettings.Resolve(...), catalog.Version);
    ///   backend.Start();                                  // resends anything an earlier run left behind
    ///   var joined = await backend.JoinAsync(raidId, joinToken);   // from the authenticator
    ///   backend.ReportExtracted(joined.PlayerId, finalTree);       // or ReportDied
    ///   await backend.EndRaidAsync();                     // then the process can exit
    ///   backend.Dispose();
    ///
    /// Threading: awaiting <see cref="JoinAsync"/> or <see cref="EndRaidAsync"/> on Unity's main thread resumes
    /// there, and the events are raised on whatever SynchronizationContext the client was created on (Unity's
    /// main thread, if it's created there). All the network work runs in the background.
    ///
    /// One raid per instance, matching one raid per server process. The raid is set by the first successful join.
    /// </summary>
    public sealed class BackendClient : IDisposable
    {
        private const string CatalogVersionHeader = "X-Catalog-Version";
        private const string IdempotencyKeyHeader = "Idempotency-Key";
        private const int MaxJoinTokenLength = 512;

        private readonly BackendSettings _settings;
        private readonly string _catalogVersion;
        private readonly BackendClientOptions _options;
        private readonly IBackendTransport _transport;
        private readonly bool _ownsTransport;
        private readonly IBackendLog _log;
        private readonly SynchronizationContext _context;
        private readonly Outbox _outbox;
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
        private readonly object _gate = new object();
        private readonly HashSet<string> _joined = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        private string _raidId;
        private bool _started;
        private bool _ending;
        private bool _aborted;
        private bool _disposed;
        private Task _endTask;
        private CancellationTokenSource _heartbeatStop;

        /// <param name="catalogVersion">The version of the item catalog this server was built with (ItemCatalog.Version).</param>
        /// <param name="transport">Leave null for HTTP; tests pass their own.</param>
        public BackendClient(BackendSettings settings, string catalogVersion, BackendClientOptions options = null, IBackendTransport transport = null, IBackendLog log = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrEmpty(catalogVersion))
            {
                throw new ArgumentException("The catalog version is required; the backend refuses calls without it.", nameof(catalogVersion));
            }

            _catalogVersion = catalogVersion;
            _options = options ?? new BackendClientOptions();
            _log = log ?? new ConsoleBackendLog();
            _ownsTransport = transport == null;
            _transport = transport ?? new HttpClientTransport(settings.BackendUrl, _options.RequestTimeout);
            _context = SynchronizationContext.Current;
            _outbox = new Outbox(settings.OutboxDirectory, SendReportAsync, _options, _log, OnReportRejected, OnRaidSettled);
        }

        /// <summary>
        /// The backend ended the raid on its own and refunded everyone still inside; nothing more counts.
        /// Send the players back to the Hideout. Raised at most once.
        /// </summary>
        public event Action<RaidAbortedEvent> RaidAborted;

        /// <summary>The backend refused a report for good. Mostly worth logging loudly; see <see cref="ReportRejectedEvent"/>.</summary>
        public event Action<ReportRejectedEvent> ReportRejected;

        /// <summary>This server's raid, once the first player has joined it.</summary>
        public string RaidId
        {
            get
            {
                lock (_gate)
                {
                    return _raidId;
                }
            }
        }

        /// <summary>Reports written but not yet answered by the backend, for this raid.</summary>
        public int PendingReports
        {
            get
            {
                var raidId = RaidId;
                return raidId == null ? 0 : _outbox.PendingCount(raidId);
            }
        }

        /// <summary>
        /// Opens the outbox and starts resending reports an earlier run of this server didn't get answered.
        /// Call once, before anything else, after subscribing to the events.
        /// </summary>
        public void Start()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_started)
                {
                    throw new InvalidOperationException("Start was already called.");
                }

                _started = true;
            }

            _outbox.Start();
            _log.Write(BackendLogLevel.Info, $"Backend client started: {_settings}.");
        }

        /// <summary>
        /// Redeems the join token a connecting player presented. On success the player's gear has left their
        /// stash for this raid, and <see cref="JoinResult.Loadout"/> is what to spawn them with. Retries network
        /// failures a few times with the same idempotency key, so a join can't move anyone's gear twice.
        /// </summary>
        public async Task<JoinResult> JoinAsync(string raidId, string joinToken, CancellationToken cancellationToken = default)
        {
            // Both come from the connecting player's machine, so check them before they go into a URL.
            if (!Uuid7.IsValid(raidId))
            {
                return JoinResult.Failed("invalid_request", "The raid ID isn't a valid ID.");
            }

            if (string.IsNullOrEmpty(joinToken) || joinToken.Length > MaxJoinTokenLength)
            {
                return JoinResult.Failed("invalid_request", "The join token is missing or too long.");
            }

            lock (_gate)
            {
                ThrowIfNotRunning();
                var refusal = JoinRefusalLocked(raidId);
                if (refusal != null)
                {
                    return refusal;
                }
            }

            var path = $"/v1/server/raids/{raidId}/join";
            var body = Wire.Compact(new JObject { ["join_token"] = joinToken });

            // Derived from the token, so a player retrying the same connection gets the same answer back.
            var key = "join-" + Wire.Sha256Hex(raidId + ":" + joinToken).Substring(0, 40);

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token))
            {
                var delay = _options.RetryInitialDelay;
                CallResult result = null;
                for (int attempt = 1; attempt <= Math.Max(1, _options.JoinAttempts); attempt++)
                {
                    if (attempt > 1)
                    {
                        await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                        delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.RetryMaxDelay.Ticks));
                    }

                    result = await CallAsync("POST", path, body, key, linked.Token).ConfigureAwait(false);
                    if (result.Status == CallStatus.Succeeded)
                    {
                        return Accept(raidId, result);
                    }

                    if (result.Status == CallStatus.Rejected)
                    {
                        return JoinResult.Failed(result.Error, result.Message);
                    }

                    _log.Write(result.IsConfigurationProblem ? BackendLogLevel.Error : BackendLogLevel.Warning,
                        $"Join for raid {raidId} failed (attempt {attempt}): {result}.");
                }

                // A wrong token or catalog is worth naming as such; everything else is the backend not answering.
                return result.IsConfigurationProblem
                    ? JoinResult.Failed(result.Error, result.Message)
                    : JoinResult.Failed("backend_unreachable", result.ToString());
            }
        }

        /// <summary>
        /// The player reached an extraction point with <paramref name="finalTree"/>: their character container
        /// and everything on it, exactly as the raid inventory has it now, including loot picked up in the raid.
        /// Written to disk before this returns; delivery happens in the background.
        /// </summary>
        public void ReportExtracted(string playerId, ItemNode finalTree, string extractPoint = null)
        {
            if (finalTree == null)
            {
                throw new ArgumentNullException(nameof(finalTree));
            }

            var body = new JObject { ["player_id"] = playerId, ["outcome"] = "extracted" };
            if (extractPoint != null)
            {
                body["extract_point"] = extractPoint;
            }

            body["inventory"] = Wire.ToJson(finalTree);
            Report(playerId, "extracted", body);
        }

        /// <summary>
        /// The player died. Their gear stays in the raid for others to loot, so the report carries no items:
        /// whatever nobody extracts is lost when the raid closes.
        /// </summary>
        public void ReportDied(string playerId)
        {
            Report(playerId, "died", new JObject { ["player_id"] = playerId, ["outcome"] = "died" });
        }

        /// <summary>
        /// The raid is over. No one else can join; anyone not yet reported dies as part of the close (no need
        /// to report them first). Completes once the backend has answered every report and the close, which
        /// may take a while if the backend is down; apply a timeout if the process must exit anyway, and the
        /// next run on this machine will finish the job from the outbox.
        /// </summary>
        public Task EndRaidAsync()
        {
            lock (_gate)
            {
                ThrowIfNotRunning();
                if (_endTask != null)
                {
                    return _endTask;
                }

                _ending = true;
                if (_raidId == null)
                {
                    // Nobody joined, so the backend never handed this server a raid it knows about. The raid it
                    // opened for the tickets expires on its own.
                    _endTask = Task.CompletedTask;
                    return _endTask;
                }

                _outbox.Enqueue(_raidId, "close", null, $"/v1/server/raids/{_raidId}/close", Wire.Compact(new JObject { ["reason"] = "finished" }));
                _endTask = _outbox.WhenSettled(_raidId);
                return _endTask;
            }
        }

        /// <summary>
        /// Stops all background work. Undelivered reports stay on disk for the next run. In the editor, call
        /// this when play mode stops, or the background work keeps running.
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _stopping.Cancel();
            _outbox.Dispose();
            if (_ownsTransport)
            {
                _transport.Dispose();
            }
        }

        private JoinResult JoinRefusalLocked(string raidId)
        {
            if (_aborted)
            {
                return JoinResult.Failed("raid_aborted", "This server's raid was aborted.");
            }

            if (_ending)
            {
                return JoinResult.Failed("raid_ending", "This server's raid is ending.");
            }

            if (_raidId != null && _raidId != raidId)
            {
                return JoinResult.Failed("wrong_raid", $"This server is running raid {_raidId}, not {raidId}.");
            }

            return null;
        }

        private JoinResult Accept(string raidId, CallResult result)
        {
            JObject json;
            ItemNode loadout;
            try
            {
                json = (JObject)Wire.Parse(result.Body);
                loadout = Wire.ToItemNode(json["loadout"]);
            }
            catch (Exception exception) when (exception is FormatException || exception is InvalidCastException || exception is Newtonsoft.Json.JsonException)
            {
                _log.Write(BackendLogLevel.Error, $"The backend's join answer couldn't be read: {exception.Message}");
                return JoinResult.Failed("invalid_response", "The backend's answer couldn't be read.");
            }

            var playerId = (string)json["player_id"];
            var displayName = (string)json["display_name"];
            bool startHeartbeat = false;
            bool isReconnect;
            lock (_gate)
            {
                // The gear has moved into the raid by now, so the server must take the player even if it
                // started ending meanwhile; refusing would strand them until the close. Only a different raid,
                // which the backend never assigns to a busy server, is a real conflict.
                if (_raidId == null)
                {
                    _raidId = raidId;
                    startHeartbeat = !_aborted && !_disposed;
                }
                else if (_raidId != raidId)
                {
                    _log.Write(BackendLogLevel.Error, $"Player {playerId} joined raid {raidId} while this server runs {_raidId}.");
                    return JoinResult.Failed("wrong_raid", $"This server is running raid {_raidId}, not {raidId}.");
                }

                if (_reported.Contains(playerId))
                {
                    // A replay of an earlier join for someone who has since left the raid.
                    return JoinResult.Failed("already_left_raid", "This player already extracted or died in this raid.");
                }

                isReconnect = !_joined.Add(playerId);
                if (startHeartbeat)
                {
                    _heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    var stop = _heartbeatStop.Token;
                    Task.Run(() => HeartbeatLoopAsync(raidId, stop));
                }
            }

            _log.Write(BackendLogLevel.Info, $"Player {playerId} ({displayName}) {(isReconnect ? "reconnected to" : "joined")} raid {raidId}.");
            return JoinResult.Joined(playerId, displayName, loadout, isReconnect);
        }

        private void Report(string playerId, string kind, JObject body)
        {
            lock (_gate)
            {
                ThrowIfNotRunning();
                if (_raidId == null || !_joined.Contains(playerId))
                {
                    throw new InvalidOperationException($"Player {playerId} hasn't joined this server's raid.");
                }

                if (_reported.Contains(playerId))
                {
                    throw new InvalidOperationException($"Player {playerId} was already reported as extracted or died.");
                }

                if (_ending)
                {
                    throw new InvalidOperationException("The raid is ending; the close report already covers everyone still inside.");
                }

                _outbox.Enqueue(_raidId, kind, playerId, $"/v1/server/raids/{_raidId}/outcomes", Wire.Compact(body));
                _reported.Add(playerId);
            }
        }

        private async Task HeartbeatLoopAsync(string raidId, CancellationToken ct)
        {
            bool failing = false;
            try
            {
                while (true)
                {
                    await Task.Delay(_options.HeartbeatInterval, ct).ConfigureAwait(false);
                    var result = await CallAsync("POST", $"/v1/server/raids/{raidId}/heartbeat", null, null, ct).ConfigureAwait(false);
                    if (result.Status == CallStatus.Succeeded)
                    {
                        if (failing)
                        {
                            _log.Write(BackendLogLevel.Info, $"Heartbeats for raid {raidId} are getting through again.");
                            failing = false;
                        }

                        continue;
                    }

                    var state = (string)Wire.TryParseObject(result.Body)?["state"];
                    if (result.Error == "raid_not_found" || (result.Error == "raid_not_active" && state == "aborted"))
                    {
                        OnRaidAborted(raidId, result.Message);
                        return;
                    }

                    if (result.Error == "raid_not_active")
                    {
                        // Closed: the close report got there first. Nothing left to keep alive.
                        return;
                    }

                    if (!failing)
                    {
                        _log.Write(result.IsConfigurationProblem ? BackendLogLevel.Error : BackendLogLevel.Warning,
                            $"Heartbeat for raid {raidId} failed: {result}. The backend aborts the raid if this lasts a whole lease.");
                        failing = true;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        private Task<CallResult> SendReportAsync(OutboxEntry entry, CancellationToken ct) =>
            CallAsync("POST", entry.Path, entry.Body, entry.Key, ct);

        /// <summary>One request. Never throws for network trouble, only when <paramref name="ct"/> is cancelled.</summary>
        private async Task<CallResult> CallAsync(string method, string path, string body, string idempotencyKey, CancellationToken ct)
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer " + _settings.ServerToken,
                [CatalogVersionHeader] = _catalogVersion,
            };

            if (idempotencyKey != null)
            {
                headers[IdempotencyKeyHeader] = idempotencyKey;
            }

            try
            {
                var response = await _transport.SendAsync(new BackendRequest(method, path, body, headers), ct).ConfigureAwait(false);
                return CallResult.From(response);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Includes HttpClient's own timeout, which surfaces as a cancellation of a token we didn't cancel.
                return CallResult.Unreachable(exception);
            }
        }

        private void OnRaidAborted(string raidId, string message)
        {
            lock (_gate)
            {
                if (_aborted)
                {
                    return;
                }

                _aborted = true;
            }

            _log.Write(BackendLogLevel.Error, $"The backend aborted raid {raidId}: {message}");
            Raise(RaidAborted, new RaidAbortedEvent(raidId, message));
        }

        private void OnReportRejected(string raidId, OutboxEntry entry, CallResult result) =>
            Raise(ReportRejected, new ReportRejectedEvent(raidId, entry.PlayerId, entry.Kind, result.StatusCode, result.Error, result.Message));

        private void OnRaidSettled(string raidId)
        {
            lock (_gate)
            {
                if (raidId == _raidId && _ending)
                {
                    // Every report and the close are answered: the raid's lease no longer matters.
                    _heartbeatStop?.Cancel();
                }
            }
        }

        private void Raise<T>(Action<T> handler, T args)
        {
            if (handler == null)
            {
                return;
            }

            void Invoke(object _)
            {
                try
                {
                    handler(args);
                }
                catch (Exception exception)
                {
                    _log.Write(BackendLogLevel.Error, $"A {typeof(T).Name} handler threw: {exception}");
                }
            }

            if (_context != null)
            {
                _context.Post(Invoke, null);
            }
            else
            {
                Invoke(null);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BackendClient));
            }
        }

        private void ThrowIfNotRunning()
        {
            ThrowIfDisposed();
            if (!_started)
            {
                throw new InvalidOperationException("Call Start before using the backend client.");
            }
        }
    }
}
