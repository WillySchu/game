using System;

namespace RaidZero.BackendApi
{
    /// <summary>Timings. The defaults suit production; tests shrink them.</summary>
    public sealed class BackendClientOptions
    {
        /// <summary>
        /// How often a live raid tells the backend it's still running. The backend aborts and refunds a raid
        /// that's silent for its lease (60 s by default), so this must stay well under that.
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>How long one HTTP request may take before it counts as failed and is retried.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>The first pause before retrying. It doubles after each failure, up to <see cref="RetryMaxDelay"/>.</summary>
        public TimeSpan RetryInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

        public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Join tries this many times before giving up, since a player is waiting at a loading screen. Outbox
        /// reports, by contrast, retry for as long as the process runs.
        /// </summary>
        public int JoinAttempts { get; set; } = 3;

        /// <summary>
        /// The map this server has loaded, sent with every join so the backend refuses a raid on another map
        /// (wrong_map) before the player's gear moves. Null skips the check.
        /// </summary>
        public string MapId { get; set; }
    }

    public enum BackendLogLevel
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>Where the client's log lines go. In Unity, map these to Debug.Log, LogWarning and LogError.</summary>
    public interface IBackendLog
    {
        void Write(BackendLogLevel level, string message);
    }

    /// <summary>Writes to standard error, which a dedicated server's log captures.</summary>
    public sealed class ConsoleBackendLog : IBackendLog
    {
        public void Write(BackendLogLevel level, string message) => Console.Error.WriteLine($"[backend] {level}: {message}");
    }
}
