using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace RaidZero.Net.Editor
{
    /// <summary>
    /// Copies shared/catalog/item_catalog.json into StreamingAssets before every build, since players and
    /// servers can't reach the repo. The copy is generated, so git ignores it.
    /// </summary>
    public sealed class CatalogBuildStep : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var source = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "shared", "catalog", "item_catalog.json"));
            if (!File.Exists(source))
            {
                throw new BuildFailedException("The item catalog isn't at " + source + ".");
            }

            Directory.CreateDirectory(Application.streamingAssetsPath);
            File.Copy(source, Path.Combine(Application.streamingAssetsPath, "item_catalog.json"), overwrite: true);
            AssetDatabase.Refresh();
        }
    }
}
