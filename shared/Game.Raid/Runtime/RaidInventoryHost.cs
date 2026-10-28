using System;
using System.Collections.Generic;
using System.Linq;
using RaidZero.Shared.Inventory;
using RaidZero.Shared.Raid;

namespace RaidZero.Raid
{
    /// <summary>
    /// The server's end of the raid inventory conversation. The network layer hands it each client's bytes
    /// with the sender's player ID (always taken from the connection, never from the message), and sends the
    /// messages it returns. Everything between, from decoding hostile input to working out who needs to see
    /// what, is here and testable without Unity.
    ///
    /// The game provides <see cref="CanReach"/>, the distance check, and calls <see cref="Tick"/> every frame or
    /// network tick: that closes containers players walked away from and sends the views that
    /// <see cref="Consume"/> deliberately holds back.
    ///
    /// Not thread-safe: call it from the server's main thread only.
    /// </summary>
    public sealed class RaidInventoryHost
    {
        private readonly RaidInventoryService _service;
        private readonly RaidInventoryHostOptions _options;
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.Ordinal);

        public RaidInventoryHost(RaidInventoryService service, RaidInventoryHostOptions options = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _options = options ?? new RaidInventoryHostOptions();
        }

        /// <summary>
        /// Whether a player is close enough to a container (playerId, containerId), using the world positions
        /// of the player and of the crate, body or pile. Checked when they open a container or pick something
        /// up off the ground, and on every <see cref="Tick"/> for containers they have open. Defaults to always.
        /// </summary>
        public Func<string, string, bool> CanReach { get; set; } = (playerId, containerId) => true;

        /// <summary>
        /// A client sent something that isn't valid protocol (playerId, reason): a bug, or a tampered client.
        /// The message was ignored. Log it; disconnecting the player after a few is reasonable.
        /// </summary>
        public event Action<string, string> ProtocolViolation;

        public RaidInventoryService Service => _service;

        /// <summary>
        /// A player joined (or reconnected) with the loadout the backend returned. Sends them their gear,
        /// and on a reconnect anything they still have open.
        /// </summary>
        public HostOutput Join(string playerId, ItemNode loadout)
        {
            _service.AddPlayer(playerId, loadout);
            if (!_sessions.ContainsKey(playerId))
            {
                _sessions[playerId] = new Session(_options.RequestBurst);
            }

            var output = new HostOutput();
            foreach (var root in _service.ViewOf(playerId))
            {
                output.Send(playerId, View(root.Id, root));
            }

            return output;
        }

        /// <summary>Handles one message from a player's client.</summary>
        /// <param name="now">Seconds on any steadily increasing clock, for rate limiting.</param>
        public HostOutput Receive(string playerId, byte[] data, double now)
        {
            RaidMessage message;
            try
            {
                message = RaidProtocol.DecodeFromClient(data);
            }
            catch (ProtocolException exception)
            {
                ProtocolViolation?.Invoke(playerId, exception.Message);
                return HostOutput.Empty;
            }

            var output = new HostOutput();
            var request = message as RaidRequest;
            if (!_service.IsActive(playerId) || !_sessions.TryGetValue(playerId, out var session))
            {
                if (request != null)
                {
                    output.Send(playerId, Refusal(request.RequestId, RaidInventoryErrorCodes.PlayerNotActive));
                }

                return output;
            }

            if (!session.TryTake(now, _options.RequestBurst, _options.RequestsPerSecond))
            {
                if (request != null)
                {
                    output.Send(playerId, Refusal(request.RequestId, RaidInventoryErrorCodes.RateLimited));
                }

                return output;
            }

            RaidInventoryResult result;
            switch (message)
            {
                case InventoryRequest inventory:
                    result = _service.Apply(playerId, inventory.Ops);
                    break;
                case PickUpRequest pickUp:
                    var pileId = _service.RootOf(pickUp.ItemId);
                    result = _service.KindOf(pileId) == RootKind.Ground && !CanReach(playerId, pileId)
                        ? null
                        : _service.PickUp(playerId, pickUp.ItemId);
                    break;
                case DropRequest drop:
                    result = _service.Drop(playerId, drop.ItemId);
                    break;
                case OpenRequest open:
                    var kind = _service.KindOf(open.ContainerId);
                    result = kind != null && kind != RootKind.Character && !CanReach(playerId, open.ContainerId)
                        ? null
                        : _service.Open(playerId, open.ContainerId);
                    break;
                case CloseRequest close:
                    _service.Close(playerId, close.ContainerId);
                    return output;
                default:
                    return output;
            }

            if (result == null)
            {
                output.Send(playerId, Refusal(request.RequestId, RaidInventoryErrorCodes.OutOfReach));
                return output;
            }

            AddUpdates(output, result);
            output.Send(playerId, new RequestResult
            {
                RequestId = request.RequestId,
                Success = result.Success,
                Error = result.Error?.Code,
                FailedOpIndex = result.FailedOpIndex,
            });
            return output;
        }

        /// <summary>
        /// The server uses up one of the player's items: rounds fired, a medkit. Unlike a request, the view
        /// isn't sent straight away (firing would otherwise resend the player's gear many times a second);
        /// <see cref="Tick"/> sends it within <see cref="RaidInventoryHostOptions.ConsumeViewDelay"/>, sooner
        /// if a request touches the same gear first. Show ammo counts from the weapon, not from the view.
        /// </summary>
        public bool Consume(string playerId, string itemId, int quantity, double now)
        {
            var result = _service.Consume(playerId, itemId, quantity);
            if (!result.Success)
            {
                return false;
            }

            foreach (var update in result.Updates)
            {
                if (_sessions.TryGetValue(update.PlayerId, out var session) && session.Dirty.Add(update.RootId) && session.Dirty.Count == 1)
                {
                    session.DirtySince = now;
                }
            }

            return true;
        }

        /// <summary>
        /// Call every frame or network tick. Closes containers players are no longer close enough to, and sends
        /// views that <see cref="Consume"/> held back once they've waited long enough.
        /// </summary>
        public HostOutput Tick(double now)
        {
            var output = new HostOutput();
            foreach (var entry in _sessions)
            {
                var playerId = entry.Key;
                var session = entry.Value;
                if (!_service.IsActive(playerId))
                {
                    continue;
                }

                foreach (var containerId in _service.OpenContainersOf(playerId).ToList())
                {
                    if (!CanReach(playerId, containerId))
                    {
                        _service.Close(playerId, containerId);
                        output.Send(playerId, new ContainerClosed { ContainerId = containerId });
                    }
                }

                if (session.Dirty.Count > 0 && now - session.DirtySince >= _options.ConsumeViewDelay)
                {
                    foreach (var rootId in session.Dirty)
                    {
                        var root = _service.Snapshot(rootId);
                        if (root != null)
                        {
                            output.Send(playerId, View(rootId, root));
                        }
                    }

                    session.Dirty.Clear();
                }
            }

            return output;
        }

        /// <summary>The player died; their gear becomes a body. Clear their client's view too.</summary>
        public void Kill(string playerId)
        {
            _service.Kill(playerId);
            _sessions.Remove(playerId);
        }

        /// <summary>The player extracted. Returns their gear for BackendClient.ReportExtracted.</summary>
        public ItemNode Extract(string playerId)
        {
            var tree = _service.Extract(playerId);
            _sessions.Remove(playerId);
            return tree;
        }

        private static RequestResult Refusal(uint requestId, string error) =>
            new RequestResult { RequestId = requestId, Success = false, Error = error };

        private ContainerView View(string rootId, ItemNode root) =>
            new ContainerView { Kind = _service.KindOf(rootId) ?? RootKind.Container, Root = root };

        /// <summary>Turns a result's updates into views for everyone who sees the changed containers.</summary>
        private void AddUpdates(HostOutput output, RaidInventoryResult result)
        {
            // Every viewer of one container gets the same bytes, so encode each view once.
            var encoded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var update in result.Updates)
            {
                if (_sessions.TryGetValue(update.PlayerId, out var session))
                {
                    session.Dirty.Remove(update.RootId);
                }

                if (update.IsRemoved)
                {
                    output.Send(update.PlayerId, new ContainerClosed { ContainerId = update.RootId });
                    continue;
                }

                if (!encoded.TryGetValue(update.RootId, out var bytes))
                {
                    bytes = RaidProtocol.Encode(View(update.RootId, update.Root));
                    encoded[update.RootId] = bytes;
                }

                output.SendBytes(update.PlayerId, bytes);
            }

            output.CreatedRootsList.AddRange(result.CreatedRoots);
            output.RemovedRootsList.AddRange(result.RemovedRoots);
        }

        private sealed class Session
        {
            private double _tokens;
            private double _last = double.NaN;

            public Session(int burst)
            {
                _tokens = burst;
            }

            /// <summary>Containers whose views Consume held back.</summary>
            public HashSet<string> Dirty { get; } = new HashSet<string>(StringComparer.Ordinal);

            public double DirtySince { get; set; }

            /// <summary>A token bucket: short bursts are fine, a sustained flood isn't.</summary>
            public bool TryTake(double now, int burst, double perSecond)
            {
                if (!double.IsNaN(_last) && now > _last)
                {
                    _tokens = Math.Min(burst, _tokens + ((now - _last) * perSecond));
                }

                _last = double.IsNaN(_last) ? now : Math.Max(_last, now);
                if (_tokens < 1)
                {
                    return false;
                }

                _tokens -= 1;
                return true;
            }
        }
    }

    public sealed class RaidInventoryHostOptions
    {
        /// <summary>How many requests a client may send at once before rate limiting starts.</summary>
        public int RequestBurst { get; set; } = 20;

        /// <summary>The sustained rate a client may send requests at.</summary>
        public double RequestsPerSecond { get; set; } = 10;

        /// <summary>How long a view changed by <see cref="RaidInventoryHost.Consume"/> may wait before it's sent.</summary>
        public double ConsumeViewDelay { get; set; } = 1.0;
    }

    /// <summary>What to send, and which ground piles to spawn or despawn in the world.</summary>
    public sealed class HostOutput
    {
        internal static readonly HostOutput Empty = new HostOutput();

        internal readonly List<OutgoingMessage> MessagesList = new List<OutgoingMessage>();
        internal readonly List<string> CreatedRootsList = new List<string>();
        internal readonly List<string> RemovedRootsList = new List<string>();

        /// <summary>Send each to its player, in this order, on a reliable ordered channel.</summary>
        public IReadOnlyList<OutgoingMessage> Messages => MessagesList;

        /// <summary>New piles on the ground: spawn a pickup for each where the dropping player stands.</summary>
        public IReadOnlyList<string> CreatedRoots => CreatedRootsList;

        /// <summary>Piles that are now empty: despawn their pickups.</summary>
        public IReadOnlyList<string> RemovedRoots => RemovedRootsList;

        internal void Send(string playerId, RaidMessage message) => SendBytes(playerId, RaidProtocol.Encode(message));

        internal void SendBytes(string playerId, byte[] data) => MessagesList.Add(new OutgoingMessage(playerId, data));
    }

    public sealed class OutgoingMessage
    {
        public OutgoingMessage(string playerId, byte[] data)
        {
            PlayerId = playerId;
            Data = data;
        }

        public string PlayerId { get; }

        public byte[] Data { get; }
    }
}
