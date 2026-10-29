using System.IO;
using Newtonsoft.Json.Linq;
using RaidZero.BackendApi;
using RaidZero.Shared.Catalog;
using UnityEngine;

namespace RaidZero.Net
{
    /// <summary>
    /// Settings for playing in the editor, from UserSettings/RaidZeroDev.json. Unity's own .gitignore keeps
    /// UserSettings/ out of the repo, so each of you can point at a different backend. The file is created
    /// on first use with values for the local dev backend (dotnet run --project backend/Game.Backend).
    /// Outside the editor there's no such file and the defaults apply: a player build never writes one next to
    /// itself (with the dev server token in it).
    /// </summary>
    public static class DevSettings
    {
        // The dev-local server token from the backend's appsettings.Development.json. Dev-only, like that file.
        private const string LocalDevServerToken = "3ogBGujpU2KY3j673e4CUY3CYXusmi72kOaph+xeRQs=";

        public static string FilePath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "UserSettings", "RaidZeroDev.json"));

        /// <summary>The backend the editor talks to, as the game server and for dev tickets.</summary>
        public static string BackendUrl => Read()["backend_url"]?.ToString() ?? "http://localhost:5080";

        public static string ServerToken => Read()["server_token"]?.ToString() ?? LocalDevServerToken;

        /// <summary>The map tickets ask for. Raids only group players asking for the same map.</summary>
        public static string MapId => Read()["map_id"]?.ToString() ?? "greybox_01";

        private static JObject Read()
        {
            if (!Application.isEditor)
            {
                return new JObject();
            }

            var path = FilePath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var defaults = new JObject
                {
                    ["backend_url"] = "http://localhost:5080",
                    ["server_token"] = LocalDevServerToken,
                    ["map_id"] = "greybox_01",
                };
                File.WriteAllText(path, defaults.ToString());
                Debug.Log("Created " + path + " with settings for the local dev backend. Edit it to use another.");
            }

            return JObject.Parse(File.ReadAllText(path));
        }
    }

    /// <summary>Where the game server finds its backend and its item catalog.</summary>
    public static class ServerConfig
    {
        /// <summary>
        /// The command line, then environment variables, as BackendSettings.Resolve reads them. In the editor,
        /// where neither is convenient, anything missing comes from <see cref="DevSettings"/> instead.
        /// </summary>
        public static BackendSettings ResolveBackend()
        {
            var outbox = Path.Combine(Application.persistentDataPath, "outbox");
            try
            {
                return BackendSettings.Resolve(System.Environment.GetCommandLineArgs(), System.Environment.GetEnvironmentVariable, outbox);
            }
            catch (BackendSettingsException) when (Application.isEditor)
            {
                return new BackendSettings(DevSettings.BackendUrl, DevSettings.ServerToken, outbox);
            }
        }

        /// <summary>Whether the command line has the switch "--name".</summary>
        public static bool HasFlag(string name) => System.Array.IndexOf(System.Environment.GetCommandLineArgs(), name) >= 0;

        /// <summary>The word given on the command line as "--name value", or null if it isn't there.</summary>
        public static string StringArgument(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        /// <summary>A whole number given on the command line as "--name 12345", or null if it isn't there.</summary>
        public static int? IntArgument(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name && int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>A number given on the command line as "--name 12.5", or null if it isn't there.</summary>
        public static float? FloatArgument(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>
        /// The repo's shared/catalog/item_catalog.json in the editor, or the copy the build step puts in
        /// StreamingAssets for players and servers.
        /// </summary>
        public static ItemCatalog LoadCatalog()
        {
            var path = Application.isEditor
                ? Path.Combine(Application.dataPath, "..", "..", "..", "shared", "catalog", "item_catalog.json")
                : Path.Combine(Application.streamingAssetsPath, "item_catalog.json");
            return CatalogJson.LoadFile(path);
        }
    }
}
