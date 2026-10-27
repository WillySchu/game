using System;
using System.Collections.Generic;

namespace RaidZero.BackendApi
{
    /// <summary>
    /// Where the backend is and how this game server proves who it is. Each setting comes from the command line
    /// if it's there, otherwise from an environment variable; the required ones fail startup if neither has them.
    ///
    ///   --backend-url https://api.yourgame.com    or  RAIDZERO_BACKEND_URL
    ///   --server-token &lt;token&gt;                     or  RAIDZERO_SERVER_TOKEN
    ///   --outbox-dir /var/lib/raidzero/outbox     or  RAIDZERO_OUTBOX_DIR   (optional; the caller picks a default)
    ///
    /// Both "--name value" and "--name=value" work. Anything else on the command line, such as Unity's own
    /// -batchmode, is ignored. Prefer the environment variable for the token on shared machines, since other
    /// users can read a process's command line.
    /// </summary>
    public sealed class BackendSettings
    {
        public const string BackendUrlArgument = "--backend-url";
        public const string ServerTokenArgument = "--server-token";
        public const string OutboxDirectoryArgument = "--outbox-dir";

        public const string BackendUrlVariable = "RAIDZERO_BACKEND_URL";
        public const string ServerTokenVariable = "RAIDZERO_SERVER_TOKEN";
        public const string OutboxDirectoryVariable = "RAIDZERO_OUTBOX_DIR";

        public BackendSettings(string backendUrl, string serverToken, string outboxDirectory)
        {
            if (!Uri.TryCreate(backendUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new BackendSettingsException($"The backend URL '{backendUrl}' isn't an absolute http or https URL.");
            }

            if (string.IsNullOrWhiteSpace(serverToken))
            {
                throw new BackendSettingsException("The server token is empty.");
            }

            if (string.IsNullOrWhiteSpace(outboxDirectory))
            {
                throw new BackendSettingsException("The outbox directory is empty.");
            }

            BackendUrl = backendUrl.TrimEnd('/');
            ServerToken = serverToken.Trim();
            OutboxDirectory = outboxDirectory;
        }

        /// <summary>Without a trailing slash, so request paths append directly.</summary>
        public string BackendUrl { get; }

        public string ServerToken { get; }

        /// <summary>Where outcome reports wait until the backend acknowledges them. One per server process.</summary>
        public string OutboxDirectory { get; }

        /// <summary>
        /// Reads the settings from <paramref name="args"/> (pass Environment.GetCommandLineArgs()), then from
        /// <paramref name="environment"/> (pass Environment.GetEnvironmentVariable). Throws
        /// <see cref="BackendSettingsException"/> naming every missing setting at once.
        /// </summary>
        public static BackendSettings Resolve(string[] args, Func<string, string> environment, string defaultOutboxDirectory)
        {
            if (environment == null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            var given = ParseArguments(args ?? Array.Empty<string>());
            string Find(string argument, string variable)
            {
                if (given.TryGetValue(argument, out var fromArgs) && !string.IsNullOrWhiteSpace(fromArgs))
                {
                    return fromArgs;
                }

                var fromEnvironment = environment(variable);
                return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
            }

            var url = Find(BackendUrlArgument, BackendUrlVariable);
            var token = Find(ServerTokenArgument, ServerTokenVariable);
            var outbox = Find(OutboxDirectoryArgument, OutboxDirectoryVariable) ?? defaultOutboxDirectory;

            var missing = new List<string>();
            if (url == null)
            {
                missing.Add($"the backend URL ({BackendUrlArgument} or {BackendUrlVariable})");
            }

            if (token == null)
            {
                missing.Add($"the server token ({ServerTokenArgument} or {ServerTokenVariable})");
            }

            if (string.IsNullOrWhiteSpace(outbox))
            {
                missing.Add($"the outbox directory ({OutboxDirectoryArgument} or {OutboxDirectoryVariable})");
            }

            if (missing.Count > 0)
            {
                throw new BackendSettingsException("The game server can't reach the backend without " + string.Join(", ", missing) + ".");
            }

            return new BackendSettings(url, token, outbox);
        }

        /// <summary>Never includes the token, so it's safe to log.</summary>
        public override string ToString() => $"backend {BackendUrl}, outbox {OutboxDirectory}";

        private static Dictionary<string, string> ParseArguments(string[] args)
        {
            var known = new[] { BackendUrlArgument, ServerTokenArgument, OutboxDirectoryArgument };
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i] ?? "";
                foreach (var name in known)
                {
                    if (arg == name && i + 1 < args.Length)
                    {
                        values[name] = args[++i];
                        break;
                    }

                    if (arg.StartsWith(name + "=", StringComparison.Ordinal))
                    {
                        values[name] = arg.Substring(name.Length + 1);
                        break;
                    }
                }
            }

            return values;
        }
    }

    /// <summary>The game server is misconfigured. The message says what to set.</summary>
    public sealed class BackendSettingsException : Exception
    {
        public BackendSettingsException(string message)
            : base(message)
        {
        }
    }
}
