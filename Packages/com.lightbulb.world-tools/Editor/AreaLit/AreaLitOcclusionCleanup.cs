using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.AreaLitOcclusion
{
    internal sealed class AreaLitOcclusionCleanupResult
    {
        public int cleanedCount;
        public int failedCount;
        public int fileCount;
        public long byteCount;
        public readonly List<string> failures = new List<string>();
    }

    [InitializeOnLoad]
    internal static class AreaLitOcclusionCleanup
    {
        private const int HistoryReceiptLimit = 10;

        private static bool cleanupScheduled;

        private sealed class Candidate
        {
            public string assetPath;
            public string absolutePath;
            public int fileCount;
            public long byteCount;
        }

        static AreaLitOcclusionCleanup()
        {
            ScheduleAutomaticCleanup();
        }

        internal static void ScheduleAutomaticCleanup()
        {
            if (cleanupScheduled) return;
            cleanupScheduled = true;
            EditorApplication.delayCall += RunAutomaticCleanup;
        }

        private static AreaLitOcclusionCleanupResult CleanFinishedTransactions()
        {
            var result = new AreaLitOcclusionCleanupResult();
            var candidates = FindCandidates();

            foreach (var candidate in candidates)
            {
                try
                {
                    if (!Directory.Exists(candidate.absolutePath)) continue;
                    if (!AssetDatabase.DeleteAsset(candidate.assetPath) && Directory.Exists(candidate.absolutePath))
                    {
                        throw new IOException("Unity could not delete the transaction asset folder.");
                    }
                    if (Directory.Exists(candidate.absolutePath))
                    {
                        throw new IOException("The transaction folder still exists after Unity reported cleanup.");
                    }

                    result.cleanedCount++;
                    result.fileCount += candidate.fileCount;
                    result.byteCount += candidate.byteCount;
                }
                catch (Exception exception)
                {
                    result.failedCount++;
                    result.failures.Add(candidate.assetPath + ": " + exception.Message);
                }
            }

            RemoveInactiveJournalScratchFiles();
            PruneCleanedHistoryReceipts();
            return result;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L) return (bytes / (1024d * 1024d * 1024d)).ToString("0.##") + " GB";
            if (bytes >= 1024L * 1024L) return (bytes / (1024d * 1024d)).ToString("0.##") + " MB";
            if (bytes >= 1024L) return (bytes / 1024d).ToString("0.##") + " KB";
            return bytes + " bytes";
        }

        private static void RunAutomaticCleanup()
        {
            cleanupScheduled = false;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                ScheduleAutomaticCleanup();
                return;
            }

            var result = CleanFinishedTransactions();
            if (result.cleanedCount > 0)
            {
                Debug.Log(
                    "[AreaLit Occlusion] Removed " + result.cleanedCount + " finished transaction folder(s), " +
                    result.fileCount + " file(s), " + FormatBytes(result.byteCount) + ".");
            }
            if (result.failedCount > 0)
            {
                Debug.LogWarning(
                    "[AreaLit Occlusion] Temporary-file cleanup will retry later for " + result.failedCount +
                    " transaction(s):\n" + string.Join("\n", result.failures.ToArray()));
            }
        }

        private static List<Candidate> FindCandidates()
        {
            var candidates = new List<Candidate>();
            if (!Directory.Exists(AreaLitOcclusionJournalStore.HistoryFolder)) return candidates;

            var active = AreaLitOcclusionJournalStore.LoadActive();
            if (AreaLitOcclusionJournalStore.HasActiveJournal && active == null)
            {
                return candidates;
            }
            foreach (var historyPath in Directory.GetFiles(AreaLitOcclusionJournalStore.HistoryFolder, "*.json"))
            {
                AreaLitOcclusionJournal journal;
                try
                {
                    journal = JsonUtility.FromJson<AreaLitOcclusionJournal>(File.ReadAllText(historyPath));
                }
                catch
                {
                    continue;
                }

                string assetPath;
                string absolutePath;
                if (!TryResolveTerminalTransaction(journal, out assetPath, out absolutePath))
                {
                    continue;
                }
                if (active != null &&
                    (string.Equals(active.transactionId, journal.transactionId, StringComparison.Ordinal) ||
                     string.Equals(NormalizeAssetPath(active.transactionAssetPath), assetPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (HasLoadedScene(assetPath))
                {
                    continue;
                }
                if (!Directory.Exists(absolutePath)) continue;

                var candidate = new Candidate
                {
                    assetPath = assetPath,
                    absolutePath = absolutePath
                };
                try
                {
                    foreach (var file in Directory.EnumerateFiles(absolutePath, "*", SearchOption.AllDirectories))
                    {
                        candidate.fileCount++;
                        try
                        {
                            candidate.byteCount += new FileInfo(file).Length;
                        }
                        catch
                        {
                            // File size is presentation-only; deletion still goes through AssetDatabase.
                        }
                    }
                }
                catch
                {
                    continue;
                }
                candidates.Add(candidate);
            }

            return candidates;
        }

        private static bool TryResolveTerminalTransaction(
            AreaLitOcclusionJournal journal,
            out string assetPath,
            out string absolutePath)
        {
            assetPath = null;
            absolutePath = null;
            if (journal == null || string.IsNullOrEmpty(journal.transactionId)) return false;
            if (!IsTerminalState(journal.state)) return false;
            if (journal.transactionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                journal.transactionId.Contains("/") || journal.transactionId.Contains("\\")) return false;

            assetPath = NormalizeAssetPath(journal.transactionAssetPath);
            var expectedPath = NormalizeAssetPath(AreaLitOcclusionPaths.TransactionsAssetPath + "/" + journal.transactionId);
            if (!string.Equals(assetPath, expectedPath, StringComparison.OrdinalIgnoreCase)) return false;

            absolutePath = AreaLitOcclusionPaths.ToAbsolutePath(assetPath);
            var expectedAbsolutePath = AreaLitOcclusionPaths.ToAbsolutePath(expectedPath);
            return string.Equals(
                Path.GetFullPath(absolutePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(expectedAbsolutePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTerminalState(string state)
        {
            return string.Equals(state, "Completed", StringComparison.Ordinal) ||
                   string.Equals(state, "Canceled", StringComparison.Ordinal) ||
                   string.Equals(state, "Recovered", StringComparison.Ordinal);
        }

        private static bool HasLoadedScene(string transactionAssetPath)
        {
            var prefix = transactionAssetPath.TrimEnd('/') + "/";
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                var scenePath = NormalizeAssetPath(scene.path);
                if (scene.isLoaded && scenePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/').TrimEnd('/');
        }

        private static void RemoveInactiveJournalScratchFiles()
        {
            if (AreaLitOcclusionJournalStore.HasActiveJournal) return;
            foreach (var suffix in new[] { ".tmp", ".previous" })
            {
                var path = AreaLitOcclusionJournalStore.ActivePath + suffix;
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[AreaLit Occlusion] Could not remove inactive journal scratch file '" + path + "'. It will be retried later.\n" + exception.Message);
                }
            }
        }

        private static void PruneCleanedHistoryReceipts()
        {
            if (!Directory.Exists(AreaLitOcclusionJournalStore.HistoryFolder)) return;

            var cleanedReceipts = new List<FileInfo>();
            foreach (var historyPath in Directory.GetFiles(AreaLitOcclusionJournalStore.HistoryFolder, "*.json"))
            {
                try
                {
                    var journal = JsonUtility.FromJson<AreaLitOcclusionJournal>(File.ReadAllText(historyPath));
                    string assetPath;
                    string absolutePath;
                    if (TryResolveTerminalTransaction(journal, out assetPath, out absolutePath) &&
                        !Directory.Exists(absolutePath))
                    {
                        cleanedReceipts.Add(new FileInfo(historyPath));
                    }
                }
                catch
                {
                    // Unreadable receipts are retained for manual diagnosis.
                }
            }

            foreach (var receipt in cleanedReceipts
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(HistoryReceiptLimit))
            {
                try
                {
                    receipt.Delete();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[AreaLit Occlusion] Could not prune old cleanup receipt '" + receipt.FullName + "'.\n" + exception.Message);
                }
            }
        }
    }
}
