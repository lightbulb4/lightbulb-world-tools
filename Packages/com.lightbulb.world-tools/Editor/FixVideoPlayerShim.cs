using System;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Lightbulb.WorldTools
{
    internal static class FixVideoPlayerShim
    {
        private const string MenuPath = "Tools/Lightbulb/Fix VideoPlayerShim URL Resolver";
        private const string LogPrefix = "[Lightbulb] ";

        [Serializable]
        private sealed class PackageManifest
        {
            public string name = null;
            public string version = null;
        }

        [MenuItem(MenuPath)]
        private static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
            {
                Debug.LogWarning(LogPrefix + "Wait until Unity is idle and outside Play Mode, then run the repair.");
                return;
            }

            string backup;
            string assetPath = "Packages/" + VideoPlayerShimResolverRepair.PackageName + "/" +
                VideoPlayerShimResolverRepair.ResolverRelativePath;
            try
            {
                var package = PackageInfo.FindForAssetPath("Packages/" + VideoPlayerShimResolverRepair.PackageName);
                if (package == null || package.name != VideoPlayerShimResolverRepair.PackageName)
                {
                    Debug.LogWarning(LogPrefix + "VideoPlayerShim is not installed. Nothing was changed.");
                    return;
                }
                if (package.source != PackageSource.Embedded)
                {
                    Debug.LogWarning(LogPrefix + "VideoPlayerShim is not an embedded project package. Cached or external packages are not modified.");
                    return;
                }

                var manifest = JsonUtility.FromJson<PackageManifest>(
                    File.ReadAllText(Path.Combine(package.resolvedPath, "package.json")));
                if (manifest == null || manifest.name != package.name || manifest.version != package.version)
                    throw new InvalidOperationException("VideoPlayerShim's package metadata does not match Unity's registration.");

                if (!AssetDatabase.IsOpenForEdit(assetPath, out string reason, StatusQueryOptions.ForceUpdate))
                    throw new InvalidOperationException("The resolver is not open for editing. " + reason);

                backup = VideoPlayerShimResolverRepair.Apply(
                    Path.GetDirectoryName(Application.dataPath), package.resolvedPath, package.version);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(LogPrefix + "VideoPlayerShim repair stopped: " + exception.Message);
                return;
            }

            if (backup == null)
            {
                Debug.Log(LogPrefix + "VideoPlayerShim already has a recognized URL resolver repair. Nothing was changed.");
                return;
            }

            // Log recovery details before requesting a script import/domain reload.
            Debug.Log(LogPrefix + "VideoPlayerShim URL resolver fixed. Direct streams bypass yt-dlp; resolver errors no longer become media URLs. " +
                "Original backup: " + backup + "\nTo undo, close Unity and copy this backup over " + assetPath +
                ". This is a source-file repair, not Unity Undo. Library backups are removed if you delete Library.");
            try
            {
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(LogPrefix + "The repair is saved, but Unity could not import it. Refresh or reopen the project. " + exception.Message);
            }
        }
    }
}
