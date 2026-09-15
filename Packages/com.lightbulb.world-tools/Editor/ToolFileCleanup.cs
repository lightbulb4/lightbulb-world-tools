using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.WorldTools
{
    internal static class ToolFileCleanup
    {
        internal const string PackedLabel = "LightbulbWorldTools.Packed";
        internal sealed class Entry
        {
            internal string Path, Reason, Hash;
            internal bool Included;
            internal ToolBackups.Copy Backup, Retained;
        }
        internal sealed class Preview
        {
            internal bool LegacyPacks, RemoveSceneBackup;
            internal readonly List<Entry> Entries = new List<Entry>();
            internal readonly List<string> Notes = new List<string>();
            internal readonly List<ToolBackups.Copy> Originals = new List<ToolBackups.Copy>();
        }
        internal static void TrackPacked(Texture2D texture)
        {
            AssetDatabase.SetLabels(texture, AssetDatabase.GetLabels(texture).Concat(new[] { PackedLabel }).Distinct().ToArray());
        }
        internal static void RequireIdle()
        {
            if (!MochieSpecularPatch.IsIdle) throw new InvalidOperationException("Wait until Unity is idle and baking has finished.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save open scenes before scanning or deleting tool files.");
            if (PrefabStageUtility.GetCurrentPrefabStage() != null) throw new InvalidOperationException("Close Prefab Mode before cleaning up tool files.");
        }
        internal static Preview Scan(bool legacyPacks, bool removeSceneBackup)
        {
            RequireIdle();
            var preview = new Preview { LegacyPacks = legacyPacks, RemoveSceneBackup = removeSceneBackup };
            foreach (var group in ToolBackups.Inventory().GroupBy(c => c.Key))
            {
                var copies = group.OrderBy(c => c.Path.StartsWith(ToolBackups.Originals + "/", StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(c => c.Time).ThenBy(c => c.Path, StringComparer.Ordinal).ToList();
                var retained = copies[0]; preview.Originals.Add(retained);
                foreach (var copy in copies.Skip(1))
                    preview.Entries.Add(new Entry { Path = copy.Path, Reason = "Old backup; retain " + retained.Path, Hash = copy.Hash, Backup = copy, Retained = retained, Included = true });
            }
            if (Directory.Exists(ToolBackups.Root + "/MochieSpecular") && AssetDatabase.GetAllAssetPaths().Count(p => p.StartsWith("Assets/") && p.EndsWith("/StandardLighting.cginc")) != 1)
                preview.Notes.Add("Legacy Mochie shader backups cannot be associated with a unique installed source. They are retained.");
            var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("l:" + PackedLabel))
                candidates[AssetDatabase.GUIDToAssetPath(guid)] = "Unused packed output";
            if (legacyPacks)
                foreach (string path in AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith("Assets/") && Regex.IsMatch(p, @"_Packed(?: \d+)?\.png$", RegexOptions.IgnoreCase)))
                    if (!candidates.ContainsKey(path)) candidates[path] = "Legacy packed filename — verify this is a generated output";
            var state = LightingExperiment.FindState(SceneManager.GetActiveScene());
            if (state != null && state.finished && !string.IsNullOrEmpty(state.materialFolder))
            {
                if (!state.materialFolder.StartsWith("Assets/LightbulbLightingExperiments/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Swapper folder is outside the tool's generated folder.");
                ToolBackups.SafePath("Assets/LightbulbLightingExperiments", state.materialFolder);
                foreach (string path in AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith(state.materialFolder + "/", StringComparison.Ordinal) && p.EndsWith(".mat")))
                    candidates[path] = "Unused material from the finished swapper setup";
                if (removeSceneBackup && state.backupScene == state.materialFolder + "/BeforeExperiment.unity" && File.Exists(state.backupScene))
                    candidates[state.backupScene] = "Pre-swap scene backup — explicitly selected for removal";
            }
            else preview.Notes.Add("To clean a finished swapper setup, open its scene after finishing the swap. Active setups are retained.");
            foreach (string stem in new[] { "Lines_Colored_Blended", "Lines_Colored_Blended_Depth" })
                foreach (string extension in new[] { ".mat", ".shader" })
                {
                    string path = "Assets/Resources/" + stem + extension;
                    if (File.Exists(path)) candidates[path] = "Legacy UV Viewer helper — review before removal";
                }
            candidates = candidates.Where(p => p.Key.StartsWith("Assets/", StringComparison.Ordinal) && File.Exists(p.Key) && !AssetDatabase.IsValidFolder(p.Key))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var protectedPaths = Referenced(candidates.Keys);
            int kept = 0;
            foreach (var pair in candidates.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string path = pair.Key;
                ToolBackups.SafePath("Assets", path);
                // Resources/StreamingAssets may be loaded by name without serialized references.
                bool legacyHelper = pair.Value.StartsWith("Legacy UV Viewer", StringComparison.Ordinal);
                if (protectedPaths.Contains(path) || (!legacyHelper && (path.Contains("/Resources/") || path.Contains("/StreamingAssets/"))) ||
                    !AssetDatabase.IsOpenForEdit(path, StatusQueryOptions.ForceUpdate) || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                { kept++; continue; }
                preview.Entries.Add(new Entry { Path = path, Reason = pair.Value, Hash = AssetHash(path), Included = !pair.Value.StartsWith("Legacy", StringComparison.Ordinal) });
            }
            if (kept != 0) preview.Notes.Add(kept + " candidate assets retained because they are referenced, dynamically loaded folders, or not editable.");
            return preview;
        }
        internal static HashSet<string> Referenced(IEnumerable<string> deletionPaths)
        {
            var deletion = new HashSet<string>(deletionPaths, StringComparer.Ordinal);
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            // Every other asset is a root, including closed scenes, prefabs, Addressables settings and materials.
            string[] roots = AssetDatabase.GetAllAssetPaths().Where(p => !deletion.Contains(p) && !AssetDatabase.IsValidFolder(p)).ToArray();
            foreach (string dependency in AssetDatabase.GetDependencies(roots, true)) referenced.Add(dependency);
            // Addressables and other registries may store GUIDs as strings, outside GetDependencies.
            var guids = deletion.Where(p => !referenced.Contains(p)).Select(p => new { Path = p, Guid = AssetDatabase.AssetPathToGUID(p) })
                .Where(p => !string.IsNullOrEmpty(p.Guid)).ToDictionary(p => p.Guid, p => p.Path, StringComparer.OrdinalIgnoreCase);
            var guidPattern = new Regex(@"\b[a-fA-F0-9]{32}\b");
            int checkedFiles = 0;
            foreach (string path in roots.Where(p => guids.Count != 0 && File.Exists(p) && new[] { ".asset", ".prefab", ".unity", ".json", ".cs" }.Contains(System.IO.Path.GetExtension(p))))
            {
                if (!Application.isBatchMode && checkedFiles++ % 100 == 0 && EditorUtility.DisplayCancelableProgressBar("Checking project references", path, (float)checkedFiles / roots.Length))
                    throw new OperationCanceledException("Reference scan cancelled; no files deleted.");
                using (var reader = new StreamReader(path))
                {
                    // Unity's binary serialized assets are covered by GetDependencies; text registries need this extra pass.
                    if (System.IO.Path.GetExtension(path) == ".asset" && reader.Peek() != '%' && reader.Peek() != '{') continue;
                    while (!reader.EndOfStream)
                        foreach (Match match in guidPattern.Matches(reader.ReadLine() ?? ""))
                            if (guids.TryGetValue(match.Value, out string referencedPath)) { referenced.Add(referencedPath); guids.Remove(match.Value); }
                }
            }
            foreach (var asset in Resources.FindObjectsOfTypeAll<UnityEngine.Object>())
                if (asset != null && EditorUtility.IsPersistent(asset) && EditorUtility.IsDirty(asset) && !deletion.Contains(AssetDatabase.GetAssetPath(asset)))
                    foreach (var dependency in EditorUtility.CollectDependencies(new[] { asset }))
                        if (dependency != null) referenced.Add(AssetDatabase.GetAssetPath(dependency));
            var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(s => s.isLoaded).ToList();
            var live = SceneReferenceScan.Collect(scenes);
            if (live.Uncertainties.Count != 0) throw new InvalidOperationException("Reference scan incomplete:\n" + string.Join("\n", live.Uncertainties));
            foreach (var obj in live.Objects) if (obj != null) referenced.Add(AssetDatabase.GetAssetPath(obj));
            return referenced;
        }
        static string AssetHash(string path) => ToolBackups.Digest(File.ReadAllBytes(path)) + ":" + ToolBackups.Digest(File.ReadAllBytes(path + ".meta"));
        internal static int Apply(Preview preview)
        {
            RequireIdle();
            var selected = preview.Entries.Where(e => e.Included).ToList();
            var fresh = Scan(preview.LegacyPacks, preview.RemoveSceneBackup);
            var current = fresh.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
            foreach (Entry entry in selected)
                if (!current.TryGetValue(entry.Path, out Entry now) || now.Hash != entry.Hash || now.Retained?.Path != entry.Retained?.Path || now.Retained?.Hash != entry.Retained?.Hash)
                    throw new InvalidOperationException("Cleanup preview changed. Scan again: " + entry.Path);
            var assets = selected.Where(e => e.Backup == null).Select(e => e.Path).ToArray();
            // Unchecked candidates become roots, so a retained material keeps its shader/texture.
            var referenced = assets.Length == 0 ? new HashSet<string>() : Referenced(assets);
            if (assets.Any(referenced.Contains)) throw new InvalidOperationException("A retained asset references a selected file. Deselect it and scan again.");
            int removed = 0;
            foreach (Entry entry in selected)
            {
                if (entry.Backup != null) ToolBackups.RemoveDuplicate(entry.Backup, entry.Retained);
                else
                {
                    ToolBackups.SafePath("Assets", entry.Path);
                    if (!AssetDatabase.DeleteAsset(entry.Path)) throw new IOException("Could not delete " + entry.Path + ". " + removed + " earlier files were removed.");
                }
                removed++;
            }
            var state = LightingExperiment.FindState(SceneManager.GetActiveScene());
            if (state != null && state.finished && !string.IsNullOrEmpty(state.materialFolder) && Directory.Exists(state.materialFolder) && !Directory.EnumerateFileSystemEntries(state.materialFolder).Any())
            {
                ToolBackups.SafePath("Assets/LightbulbLightingExperiments", state.materialFolder);
                if (!AssetDatabase.DeleteAsset(state.materialFolder)) throw new IOException("Could not remove empty swapper folder.");
            }
            return removed;
        }
    }
}
