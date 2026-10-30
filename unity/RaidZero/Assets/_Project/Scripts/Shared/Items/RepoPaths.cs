using System.IO;
using UnityEngine;

namespace RaidZero.Items
{
    /// <summary>
    /// Files in the repo outside the Unity project, for the editor and editor tools (players and servers can't
    /// reach the repo, so they use copies in StreamingAssets).
    /// </summary>
    public static class RepoPaths
    {
        /// <summary>The repo's shared/catalog/item_catalog.json.</summary>
        public static string CatalogFile => Find(Path.Combine("shared", "catalog", "item_catalog.json"));

        /// <summary>
        /// A path relative to the repo root, found by walking up from the project's Assets folder. Multiplayer
        /// Play Mode's extra players run from a copy of the project inside Library (Library/VP/...), so a fixed
        /// number of ".." from Assets would land in Library for them. If nothing up the tree has the file, it's
        /// where the main project would expect it, so error messages name the usual place.
        /// </summary>
        public static string Find(string relativeToRepo)
        {
            for (var dir = new DirectoryInfo(Application.dataPath); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativeToRepo);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", relativeToRepo));
        }
    }
}
