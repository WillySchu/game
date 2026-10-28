using System;
using System.Collections.Generic;

namespace RaidZero.Raid
{
    /// <summary>What an <see cref="ExtractionTracker"/> update did to a player's countdown.</summary>
    public enum ExtractionChange
    {
        None,

        /// <summary>A countdown began (they stepped into a zone, moved to another, or were interrupted and are still inside).</summary>
        Started,

        /// <summary>They left the zone before the countdown finished.</summary>
        Cancelled,

        /// <summary>The countdown finished. Extract them.</summary>
        Completed,
    }

    /// <summary>
    /// The extraction countdowns: a player extracts by staying in one extraction zone for its whole countdown.
    /// Leaving the zone cancels it; <see cref="Interrupt"/> (taking damage) starts it over. No engine in it:
    /// the server works out which zone each player stands in and reports it every frame.
    /// </summary>
    public sealed class ExtractionTracker
    {
        private readonly Dictionary<string, Countdown> _countdowns = new Dictionary<string, Countdown>(StringComparer.Ordinal);

        /// <summary>The zone the player is extracting at, or null.</summary>
        public string ZoneOf(string playerId) =>
            playerId != null && _countdowns.TryGetValue(playerId, out var countdown) ? countdown.Zone : null;

        /// <summary>Seconds until the player extracts, or null if they aren't extracting.</summary>
        public double? SecondsLeft(string playerId, double now) =>
            playerId != null && _countdowns.TryGetValue(playerId, out var countdown)
                ? Math.Max(0, countdown.StartedAt + countdown.Seconds - now)
                : (double?)null;

        /// <summary>
        /// Where the player is now: the zone they're standing in (null for none) and how long that zone's
        /// countdown is.
        /// </summary>
        public ExtractionChange Update(string playerId, string zone, double seconds, double now)
        {
            if (playerId == null)
            {
                throw new ArgumentNullException(nameof(playerId));
            }

            _countdowns.TryGetValue(playerId, out var current);
            if (zone == null)
            {
                return current != null && _countdowns.Remove(playerId) ? ExtractionChange.Cancelled : ExtractionChange.None;
            }

            if (current == null || !string.Equals(current.Zone, zone, StringComparison.Ordinal))
            {
                _countdowns[playerId] = new Countdown(zone, seconds, now);
                return ExtractionChange.Started;
            }

            if (now >= current.StartedAt + current.Seconds)
            {
                _countdowns.Remove(playerId);
                return ExtractionChange.Completed;
            }

            return ExtractionChange.None;
        }

        /// <summary>Starts the player's countdown over, if they have one. True if it did.</summary>
        public bool Interrupt(string playerId, double now)
        {
            if (playerId == null || !_countdowns.TryGetValue(playerId, out var current))
            {
                return false;
            }

            _countdowns[playerId] = new Countdown(current.Zone, current.Seconds, now);
            return true;
        }

        /// <summary>The player is gone (extracted, dead or disconnected). Drops their countdown without reporting it.</summary>
        public void Forget(string playerId)
        {
            if (playerId != null)
            {
                _countdowns.Remove(playerId);
            }
        }

        private sealed class Countdown
        {
            public Countdown(string zone, double seconds, double startedAt)
            {
                Zone = zone;
                Seconds = seconds;
                StartedAt = startedAt;
            }

            public string Zone { get; }

            public double Seconds { get; }

            public double StartedAt { get; }
        }
    }
}
