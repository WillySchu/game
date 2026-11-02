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
        /// The map this server has loaded. It's sent with every join so the backend refuses a raid on another
        /// map (wrong_map) before the player's gear moves, and it's what the server's ready reports say it's
        /// waiting with. Null skips the check and sends no ready reports.
        /// </summary>
        public string MapId { get; set; }

        /// <summary>
        /// How often a server that's waiting for its raid tells the backend it's up (POST /v1/server/ready),
        /// from Start until its first player joins. The backend says how often it wants them
        /// (report_every_seconds), and the shorter of the two wins.
        /// </summary>
        public TimeSpan ReadyInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// What this server was built from (a commit, say), shown in the backend's admin view. Defaults to the
        /// RAIDZERO_BUILD environment variable. Null leaves it out.
        /// </summary>
        public string Build { get; set; } = Environment.GetEnvironmentVariable("RAIDZERO_BUILD");

        /// <summary>
        /// The fingerprint of this server's networked code, sent with its ready reports. Players whose client
        /// sends a different one aren't given raids here. Null: the backend doesn't check.
        /// </summary>
        public string NetworkCode { get; set; }
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
