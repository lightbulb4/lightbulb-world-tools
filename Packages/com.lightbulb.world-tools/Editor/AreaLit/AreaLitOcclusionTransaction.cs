using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.AreaLitOcclusion
{
    [Serializable]
    internal sealed class JournalSceneSetup
    {
        public string path;
        public bool isLoaded;
        public bool isActive;
    }

    [Serializable]
    internal sealed class JournalSceneCopy
    {
        public string sourcePath;
        public string stagingPath;
    }

    [Serializable]
    internal sealed class JournalEmitter
    {
        public ObjectLocator rendererLocator;
        public int materialSlot;
        public int sourceSubmesh;
        public bool rendererWasEnabled;
        public bool selected;
        public OcclusionChannel channel;
        public float bakeIntensity;
    }

    [Serializable]
    internal sealed class JournalFileBackup
    {
        public string assetPath;
        public string backupAbsolutePath;
        public bool existed;
    }

    [Serializable]
    internal sealed class AreaLitOcclusionJournal
    {
        public int version = 5;
        public string transactionId;
        public string createdUtc;
        public string state;
        public string error;
        public string activeSourceScenePath;
        public string transactionAssetPath;
        public string bakeOutputAssetPath;
        public string generatedOutputAssetPath;
        public string previousBakeryOutputPath;
        public bool previousBakeryUseScenePath;
        public bool previousDeletePreviousLightmaps;
        public bool bakerySettingsCaptured;
        public bool applyToMaterials;
        public bool outputAdjustmentsCaptured;
        public float outputBrightness = 1f;
        public float outputContrast = 1f;
        public bool bakeIntensityMultiplierCaptured;
        public float bakeIntensityMultiplier = 1f;
        public List<JournalSceneSetup> originalSceneSetup = new List<JournalSceneSetup>();
        public List<JournalSceneCopy> sceneCopies = new List<JournalSceneCopy>();
        public List<JournalEmitter> emitters = new List<JournalEmitter>();
        public List<string> receiverMaterialPaths = new List<string>();
        public List<JournalFileBackup> outputBackups = new List<JournalFileBackup>();
        public List<JournalFileBackup> materialBackups = new List<JournalFileBackup>();
        public List<string> publishedOutputPaths = new List<string>();
    }

    internal static class AreaLitOcclusionJournalStore
    {
        internal static string LibraryRoot
        {
            get { return Path.Combine(AreaLitOcclusionPaths.ProjectRoot, "Library", "AreaLitOcclusion"); }
        }

        internal static string ActivePath
        {
            get { return Path.Combine(LibraryRoot, "active-transaction.json"); }
        }

        internal static string HistoryFolder
        {
            get { return Path.Combine(LibraryRoot, "History"); }
        }

        public static bool HasActiveJournal
        {
            get { return File.Exists(ActivePath); }
        }

        public static AreaLitOcclusionJournal LoadActive()
        {
            if (!File.Exists(ActivePath)) return null;
            try
            {
                return JsonUtility.FromJson<AreaLitOcclusionJournal>(File.ReadAllText(ActivePath));
            }
            catch (Exception exception)
            {
                Debug.LogError("[AreaLit Occlusion] Recovery journal could not be read. It was left in place at " + ActivePath + "\n" + exception);
                return null;
            }
        }

        public static void Save(AreaLitOcclusionJournal journal)
        {
            Directory.CreateDirectory(LibraryRoot);
            var temporaryPath = ActivePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(journal, true));

            if (File.Exists(ActivePath))
            {
                // Do not delete the previous journal before the replacement is durable. If the
                // filesystem cannot perform the atomic replacement, the caller stops before its
                // next mutation and both the old journal and new .tmp file remain recoverable.
                var previousPath = ActivePath + ".previous";
                File.Replace(temporaryPath, ActivePath, previousPath);
            }
            else
            {
                File.Move(temporaryPath, ActivePath);
            }
        }

        public static void Archive(AreaLitOcclusionJournal journal)
        {
            journal.state = string.IsNullOrEmpty(journal.state) ? "Archived" : journal.state;
            Save(journal);

            var historyFolder = HistoryFolder;
            Directory.CreateDirectory(historyFolder);
            var historyPath = Path.Combine(historyFolder, journal.transactionId + ".json");
            if (File.Exists(historyPath))
            {
                historyPath = Path.Combine(historyFolder, journal.transactionId + "-" + DateTime.UtcNow.Ticks + ".json");
            }
            File.Move(ActivePath, historyPath);
            AreaLitOcclusionCleanup.ScheduleAutomaticCleanup();
        }
    }

    internal static class AreaLitOcclusionAssetFile
    {
        private static readonly int[] ReplaceRetryDelaysMilliseconds = { 0, 25, 100, 250, 750, 1500 };

        public static void ReplaceWithCopy(string sourceAbsolutePath, string destinationAbsolutePath)
        {
            if (string.IsNullOrEmpty(sourceAbsolutePath) || !File.Exists(sourceAbsolutePath))
            {
                throw new FileNotFoundException("The replacement source file is missing.", sourceAbsolutePath);
            }
            if (string.IsNullOrEmpty(destinationAbsolutePath))
            {
                throw new ArgumentException("A destination path is required.", "destinationAbsolutePath");
            }

            var destinationDirectory = Path.GetDirectoryName(destinationAbsolutePath);
            if (string.IsNullOrEmpty(destinationDirectory))
            {
                throw new InvalidOperationException("The replacement destination has no parent directory: " + destinationAbsolutePath);
            }

            Directory.CreateDirectory(destinationDirectory);
            var temporaryPath = destinationAbsolutePath + ".arealit-write-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(sourceAbsolutePath, temporaryPath, false);
                CommitTemporaryFile(temporaryPath, destinationAbsolutePath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        public static void CommitTemporaryFile(string temporaryPath, string destinationAbsolutePath)
        {
            if (string.IsNullOrEmpty(temporaryPath) || !File.Exists(temporaryPath))
            {
                throw new FileNotFoundException("The prepared replacement file is missing.", temporaryPath);
            }

            if (File.Exists(destinationAbsolutePath))
            {
                // Replace only the asset bytes. The adjacent .meta file is deliberately untouched,
                // so the stable asset GUID and every material reference remain intact.
                IOException lastSharingFailure = null;
                for (var attempt = 0; attempt < ReplaceRetryDelaysMilliseconds.Length; attempt++)
                {
                    if (attempt > 0)
                    {
                        System.Threading.Thread.Sleep(ReplaceRetryDelaysMilliseconds[attempt]);
                    }

                    // Imported textures can remain memory-mapped briefly after an import. Unity
                    // documents this call as the supported way to release its cached handles before
                    // modifying asset files; retrying also gives an import worker time to let go.
                    AssetDatabase.ReleaseCachedFileHandles();
                    try
                    {
                        File.Replace(temporaryPath, destinationAbsolutePath, null);
                        if (attempt > 0)
                        {
                            Debug.Log(
                                "[AreaLit Occlusion] Unity temporarily held an output texture open; " +
                                "the atomic replacement succeeded on retry " + (attempt + 1) + ".");
                        }
                        return;
                    }
                    catch (IOException exception)
                    {
                        lastSharingFailure = exception;
                        if (!File.Exists(temporaryPath)) throw;
                    }
                }

                throw new IOException(
                    "Unity kept the destination texture open through " +
                    ReplaceRetryDelaysMilliseconds.Length +
                    " safe replacement attempts. The original file was left intact.",
                    lastSharingFailure);
            }
            else
            {
                File.Move(temporaryPath, destinationAbsolutePath);
            }
        }
    }

    [InitializeOnLoad]
    internal static class AreaLitOcclusionRecovery
    {
        private static bool promptScheduled;

        static AreaLitOcclusionRecovery()
        {
            ScheduleRecoveryCheck();
        }

        public static void ScheduleRecoveryCheck()
        {
            if (promptScheduled || !AreaLitOcclusionJournalStore.HasActiveJournal) return;
            promptScheduled = true;
            EditorApplication.delayCall += PromptForRecovery;
        }

        private static void PromptForRecovery()
        {
            promptScheduled = false;
            var journal = AreaLitOcclusionJournalStore.LoadActive();
            if (journal == null) return;

            if (journal.state == "Completed" || journal.state == "Canceled" || journal.state == "Recovered")
            {
                AreaLitOcclusionJournalStore.Archive(journal);
                return;
            }

            if (string.Equals(journal.state, "Baking", StringComparison.Ordinal) &&
                AreaLitOcclusionBakery.BakeInProgress)
            {
                AreaLitOcclusionBakeController.ResumeMonitoring(journal);
                return;
            }

            if (string.Equals(journal.state, "InspectionReady", StringComparison.Ordinal) &&
                AreaLitOcclusionBakeController.TryResumePrepared(journal))
            {
                return;
            }

            var restore = EditorUtility.DisplayDialog(
                "AreaLit Occlusion Recovery",
                "An occlusion bake did not finish cleanly. The original scenes and their light settings were never modified. " +
                "Restore the original scene setup and roll back any incomplete material/output publication now?",
                "Restore Now",
                "Open Tool");

            if (restore)
            {
                TryRecover(journal, true);
            }
            else
            {
                AreaLitOcclusionWindow.OpenWindow();
            }
        }

        public static bool TryRecoverActive(bool showDialogs)
        {
            var journal = AreaLitOcclusionJournalStore.LoadActive();
            if (journal == null) return true;
            return TryRecover(journal, showDialogs);
        }

        public static bool TryRecover(AreaLitOcclusionJournal journal, bool showDialogs)
        {
            try
            {
                journal.state = "Recovering";
                AreaLitOcclusionJournalStore.Save(journal);
                RollBackPublishedFiles(journal);
                RestoreBakerySettings(journal);
                RestoreOriginalSceneSetup(journal);
                journal.state = "Recovered";
                journal.error = string.IsNullOrEmpty(journal.error) ? "Recovered after an interrupted bake." : journal.error;
                AreaLitOcclusionJournalStore.Archive(journal);
                Debug.Log("[AreaLit Occlusion] Recovery completed. Original scenes are open and incomplete asset changes were rolled back.");
                if (showDialogs)
                {
                    EditorUtility.DisplayDialog("AreaLit Occlusion", "Recovery completed. Your original scenes and assets are restored.", "OK");
                }
                return true;
            }
            catch (Exception exception)
            {
                journal.state = "RecoveryRequired";
                journal.error = exception.ToString();
                AreaLitOcclusionJournalStore.Save(journal);
                Debug.LogError("[AreaLit Occlusion] Automatic recovery stopped without discarding the recovery journal.\n" + exception);
                if (showDialogs)
                {
                    EditorUtility.DisplayDialog(
                        "AreaLit Occlusion Recovery Needs Attention",
                        "Recovery stopped safely and kept all backup data. Resolve any unsaved non-staging scenes, then press Restore again.\n\n" + exception.Message,
                        "OK");
                }
                return false;
            }
        }

        public static void RestoreBakerySettings(AreaLitOcclusionJournal journal)
        {
            if (journal == null || !journal.bakerySettingsCaptured) return;

            if (!AreaLitOcclusionBakery.IsAvailable)
            {
                Debug.LogWarning(
                    "[AreaLit Occlusion] Bakery became unavailable during recovery, so only scene and asset state will be restored. " +
                    "The previous Bakery output settings remain recorded in the archived transaction journal.");
            }
            else
            {
                AreaLitOcclusionBakery.OutputPath = journal.previousBakeryOutputPath;
                AreaLitOcclusionBakery.UseScenePath = journal.previousBakeryUseScenePath;
                var settings = AreaLitOcclusionBakery.GetProjectSettings();
                AreaLitOcclusionBakery.SetDeletePreviousLightmaps(
                    settings,
                    journal.previousDeletePreviousLightmaps);
            }

            journal.bakerySettingsCaptured = false;
            AreaLitOcclusionJournalStore.Save(journal);
        }

        public static void RestoreOriginalSceneSetup(AreaLitOcclusionJournal journal)
        {
            if (journal == null || journal.originalSceneSetup.Count == 0) return;

            var stagingPaths = new HashSet<string>(journal.sceneCopies.Select(copy => copy.stagingPath), StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !scene.isDirty) continue;

                if (stagingPaths.Contains(scene.path))
                {
                    // Staging scenes are tool-owned and their current state is already saved in the
                    // transaction folder. Saving them again prevents Unity from asking to save them
                    // over a user's unrelated scene while recovery restores the originals.
                    if (!EditorSceneManager.SaveScene(scene))
                    {
                        throw new IOException("Unity could not save staging scene before recovery: " + scene.path);
                    }
                }
                else
                {
                    throw new InvalidOperationException("Scene '" + scene.path + "' has unsaved user changes. Save it before restoring the occlusion transaction.");
                }
            }

            foreach (var setup in journal.originalSceneSetup)
            {
                if (!File.Exists(AreaLitOcclusionPaths.ToAbsolutePath(setup.path)))
                {
                    throw new FileNotFoundException("An original scene required for recovery is missing.", setup.path);
                }
            }

            var originalSetup = journal.originalSceneSetup.Select(setup => new SceneSetup
            {
                path = setup.path,
                isLoaded = setup.isLoaded,
                isActive = setup.isActive
            }).ToArray();

            EditorSceneManager.RestoreSceneManagerSetup(originalSetup);
        }

        private static void RollBackPublishedFiles(AreaLitOcclusionJournal journal)
        {
            RollBackFileList(journal.materialBackups);
            RollBackFileList(journal.outputBackups);
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        private static void RollBackFileList(List<JournalFileBackup> backups)
        {
            for (var index = backups.Count - 1; index >= 0; index--)
            {
                var backup = backups[index];
                if (backup.existed)
                {
                    if (!File.Exists(backup.backupAbsolutePath))
                    {
                        throw new FileNotFoundException("A recovery backup is missing.", backup.backupAbsolutePath);
                    }

                    AreaLitOcclusionAssetFile.ReplaceWithCopy(
                        backup.backupAbsolutePath,
                        AreaLitOcclusionPaths.ToAbsolutePath(backup.assetPath));
                    AssetDatabase.ImportAsset(backup.assetPath, ImportAssetOptions.ForceUpdate);
                }
                else if (!string.IsNullOrEmpty(backup.assetPath) && AssetDatabase.LoadMainAssetAtPath(backup.assetPath) != null)
                {
                    AssetDatabase.DeleteAsset(backup.assetPath);
                }
                else if (!string.IsNullOrEmpty(backup.assetPath))
                {
                    var absolutePath = AreaLitOcclusionPaths.ToAbsolutePath(backup.assetPath);
                    if (File.Exists(absolutePath)) File.Delete(absolutePath);
                    var metaPath = absolutePath + ".meta";
                    if (File.Exists(metaPath)) File.Delete(metaPath);
                }
            }
        }
    }
}
