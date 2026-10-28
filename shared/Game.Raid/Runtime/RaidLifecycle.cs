using System;
using System.Collections.Generic;
using RaidZero.Shared.Raid;

namespace RaidZero.Raid
{
    public sealed class RaidLifecycleOptions
    {
        /// <summary>How long a raid runs from its first join. Anyone still inside when it runs out is missing in action.</summary>
        public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(25);

        /// <summary>A raid nobody inside is connected to ends after this long, rather than running out the clock empty.</summary>
        public TimeSpan EmptyGrace { get; set; } = TimeSpan.FromMinutes(3);
    }

    /// <summary>
    /// The rules for when a raid starts and ends, with no networking or engine in it. The game server tells it
    /// what happens (joins, disconnects, extractions, deaths) and calls <see cref="Tick"/> every frame; when
    /// <see cref="Phase"/> becomes <see cref="RaidPhase.Ending"/>, the server tells the players still inside
    /// they're missing in action and closes the raid with the backend.
    ///
    /// Times are seconds on any steadily increasing clock (the server uses Time.realtimeSinceStartupAsDouble).
    /// </summary>
    public sealed class RaidLifecycle
    {
        private readonly RaidLifecycleOptions _options;
        private readonly Dictionary<string, bool> _inside = new Dictionary<string, bool>(StringComparer.Ordinal); // player -> connected
        private readonly HashSet<string> _left = new HashSet<string>(StringComparer.Ordinal);
        private int _joinsInFlight;
        private double? _emptySince;

        public RaidLifecycle(RaidLifecycleOptions options = null)
        {
            _options = options ?? new RaidLifecycleOptions();
        }

        public RaidPhase Phase { get; private set; } = RaidPhase.Waiting;

        public RaidEndReason EndReason { get; private set; }

        /// <summary>When the first player joined. Null while <see cref="RaidPhase.Waiting"/>.</summary>
        public double? StartedAt { get; private set; }

        public TimeSpan Duration => _options.Duration;

        /// <summary>Players who joined and haven't extracted or died, connected or not.</summary>
        public IEnumerable<string> PlayersInside => _inside.Keys;

        public bool IsInside(string playerId) => playerId != null && _inside.ContainsKey(playerId);

        /// <summary>Seconds left on the clock: the full duration while waiting, zero once it's over.</summary>
        public double SecondsRemaining(double now)
        {
            switch (Phase)
            {
                case RaidPhase.Waiting:
                    return _options.Duration.TotalSeconds;
                case RaidPhase.InProgress:
                    return Math.Max(0, StartedAt.Value + _options.Duration.TotalSeconds - now);
                default:
                    return 0;
            }
        }

        /// <summary>
        /// A player is redeeming a ticket. False once the raid is ending: refuse them before their gear moves.
        /// Every true must be followed by <see cref="EndJoin"/>, whatever the outcome.
        /// </summary>
        public bool TryBeginJoin()
        {
            if (Phase == RaidPhase.Ending || Phase == RaidPhase.Closed)
            {
                return false;
            }

            _joinsInFlight++;
            return true;
        }

        /// <summary>
        /// The join attempt from <see cref="TryBeginJoin"/> is over. Call <see cref="PlayerJoined"/> first if it
        /// succeeded. Until every join in flight has finished, the raid won't end early.
        /// </summary>
        public void EndJoin()
        {
            if (_joinsInFlight > 0)
            {
                _joinsInFlight--;
            }
        }

        /// <summary>
        /// The backend accepted the player's ticket, so their gear is in the raid. <paramref name="connected"/>
        /// is false if their connection dropped while the backend answered: they're inside, just not here. The
        /// first join starts the clock. Joining again after a disconnect is fine; joining after leaving isn't.
        /// </summary>
        public void PlayerJoined(string playerId, bool connected, double now)
        {
            if (playerId == null)
            {
                throw new ArgumentNullException(nameof(playerId));
            }

            if (Phase == RaidPhase.Closed || _left.Contains(playerId))
            {
                return;
            }

            if (Phase == RaidPhase.Waiting)
            {
                Phase = RaidPhase.InProgress;
                StartedAt = now;
            }

            _inside[playerId] = connected;
        }

        /// <summary>Their connection dropped. Their character stays where it was, and they can come back with the same ticket.</summary>
        public void PlayerDisconnected(string playerId)
        {
            if (playerId != null && _inside.ContainsKey(playerId))
            {
                _inside[playerId] = false;
            }
        }

        /// <summary>They extracted or died, so they're out of the raid for good.</summary>
        public void PlayerLeft(string playerId)
        {
            if (playerId != null && _inside.Remove(playerId))
            {
                _left.Add(playerId);
            }
        }

        /// <summary>Ends the raid now, whatever the clock says (the backend aborted it).</summary>
        public void End(RaidEndReason reason, double now)
        {
            if (Phase == RaidPhase.Ending || Phase == RaidPhase.Closed)
            {
                return;
            }

            StartedAt = StartedAt ?? now;
            Phase = RaidPhase.Ending;
            EndReason = reason;
        }

        /// <summary>The backend has the close and every result. Nothing changes after this.</summary>
        public void MarkClosed()
        {
            if (Phase != RaidPhase.Ending)
            {
                throw new InvalidOperationException("A raid closes only after it has ended (it's " + Phase + ").");
            }

            Phase = RaidPhase.Closed;
        }

        /// <summary>Checks the clock and who's left. Returns true if this call moved the raid to Ending.</summary>
        public bool Tick(double now)
        {
            if (Phase != RaidPhase.InProgress)
            {
                return false;
            }

            if (now >= StartedAt.Value + _options.Duration.TotalSeconds)
            {
                return EndBecause(RaidEndReason.TimeUp);
            }

            if (_joinsInFlight > 0)
            {
                return false;
            }

            if (_inside.Count == 0)
            {
                return EndBecause(RaidEndReason.EveryoneLeft);
            }

            if (_inside.ContainsValue(true))
            {
                _emptySince = null;
                return false;
            }

            _emptySince = _emptySince ?? now;
            return now - _emptySince.Value >= _options.EmptyGrace.TotalSeconds && EndBecause(RaidEndReason.Abandoned);
        }

        private bool EndBecause(RaidEndReason reason)
        {
            Phase = RaidPhase.Ending;
            EndReason = reason;
            return true;
        }
    }
}
