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
    // Temporary upgrade migration. Remove this class and ToolBackups' legacy inventory reader after the migration release.
    [InitializeOnLoad]
    internal static class LegacyToolFileMigration
    {
        static readonly string CompletionKey = "LightbulbWorldTools.LegacyFiles.v1." + ToolBackups.Digest(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(".")));
        static readonly double EarliestRun = EditorApplication.timeSinceStartup + 5;
        static LegacyToolFileMigration()
        {
            if (!Application.isBatchMode && !EditorPrefs.GetBool(CompletionKey)) EditorApplication.update += WhenIdle;
        }
        static void WhenIdle()
        {
            if (EditorApplication.timeSinceStartup < EarliestRun || !MochieSpecularPatch.IsIdle || PrefabStageUtility.GetCurrentPrefabStage() != null) return;
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) return;
            EditorApplication.update -= WhenIdle;
            try
            {
                int removed = MigrateBackups() + RemoveOldUVHelpers();
                EditorPrefs.SetBool(CompletionKey, true);
                if (removed != 0) Debug.Log("[Lightbulb] One-time legacy cleanup removed " + removed + " obsolete files. Original backups and referenced assets were retained.");
            }
            catch (Exception e) { Debug.LogWarning("[Lightbulb] Legacy cleanup stopped: " + e.Message + ". It will retry after the next editor reload."); }
        }
        static int MigrateBackups()
        {
            int removed = 0;
            foreach (var group in ToolBackups.Inventory().GroupBy(c => c.Key))
            {
                var copies = group.OrderBy(c => c.Path.StartsWith(ToolBackups.Originals + "/", StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(c => c.Time).ThenBy(c => c.Path, StringComparer.Ordinal).ToList();
                var original = copies[0];
                byte[] bytes = File.ReadAllBytes(ToolBackups.IOPath(original.Path));
                if (ToolBackups.Digest(bytes) != original.Hash) throw new IOException("Backup changed during migration: " + original.Path);
                // Move the retained original into the permanent store before deleting any old run copies.
                string path = ToolBackups.Preserve(original.Source, bytes, new List<ToolBackups.Copy>());
                var retained = ToolBackups.Read(path, original.Source, original.Key);
                if (retained.Hash != original.Hash) throw new IOException("Original backup verification failed: " + path);
                foreach (var copy in copies.Where(c => c.Path != path)) { ToolBackups.RemoveDuplicate(copy, retained); removed++; }
            }
            return removed;
        }
        static string Normalize(string text) => Regex.Replace(text, @"\s+", "");
        static int RemoveOldUVHelpers()
        {
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (bool depth in new[] { false, true })
            {
                string stem = "Assets/Resources/Lines_Colored_Blended" + (depth ? "_Depth" : "");
                string shaderPath = stem + ".shader", materialPath = stem + ".mat";
                string name = "Lines/Colored Blended" + (depth ? " with DepthTest" : "");
                string expected = "Shader \"" + name + "\" {SubShader { Pass {BindChannels { Bind \"Color\",color }Blend SrcAlpha OneMinusSrcAlpha" +
                    " ZWrite On Cull Back ZTest " + (depth ? "LEqual" : "Always") + " Fog { Mode Off }} } }";
                if (!File.Exists(shaderPath) || Normalize(File.ReadAllText(shaderPath)) != Normalize(expected)) continue;
                candidates.Add(shaderPath);
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null || AssetDatabase.GetAssetPath(material.shader) != shaderPath || material.name != name ||
                    material.shaderKeywords.Length != 0 || material.enableInstancing || material.doubleSidedGI || material.hideFlags != HideFlags.None || material.renderQueue != material.shader.renderQueue) continue;
                var serialized = new SerializedObject(material);
                if (new[] { "m_TexEnvs", "m_Ints", "m_Floats", "m_Colors" }.All(p =>
                    (serialized.FindProperty("m_SavedProperties." + p)?.arraySize ?? 0) == 0)) candidates.Add(materialPath);
            }
            candidates.RemoveWhere(p => !AssetDatabase.IsOpenForEdit(p, StatusQueryOptions.ForceUpdate) || (File.GetAttributes(p) & FileAttributes.ReadOnly) != 0);
            if (candidates.Count == 0) return 0;
            var retained = Referenced(candidates);
            // Retained helpers become roots too, so a retained material keeps its shader.
            foreach (string path in retained.Where(candidates.Contains).ToArray()) retained.UnionWith(AssetDatabase.GetDependencies(path, true));
            int removed = 0;
            foreach (string path in candidates.Where(p => !retained.Contains(p)))
            {
                ToolBackups.SafePath("Assets/Resources", path);
                if (!AssetDatabase.DeleteAsset(path)) throw new IOException("Could not remove obsolete UV helper: " + path);
                removed++;
            }
            return removed;
        }
        static HashSet<string> Referenced(IEnumerable<string> deletionPaths)
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
            foreach (string path in roots.Where(p => guids.Count != 0 && File.Exists(p) && new[] { ".asset", ".prefab", ".unity", ".json", ".cs" }.Contains(System.IO.Path.GetExtension(p))))
            {
                using (var reader = new StreamReader(path))
                {
                    // Unity's binary serialized assets are covered by GetDependencies; text registries need this extra pass.
                    if (System.IO.Path.GetExtension(path) == ".asset" && reader.Peek() != '%' && reader.Peek() != '{') continue;
                    while (!reader.EndOfStream)
                        foreach (Match match in guidPattern.Matches(reader.ReadLine() ?? ""))
                            if (guids.TryGetValue(match.Value, out string referencedPath)) { referenced.Add(referencedPath); guids.Remove(match.Value); }
                }
            }
            foreach (string path in roots.Where(p => p.StartsWith("Assets/") && File.Exists(p) && new[] { ".cs", ".json" }.Contains(Path.GetExtension(p))))
            {
                string text = File.ReadAllText(path);
                if (text.Contains("Lines_Colored_Blended") || text.Contains("Lines/Colored Blended")) referenced.UnionWith(deletion);
            }
            foreach (var asset in Resources.FindObjectsOfTypeAll<UnityEngine.Object>())
                if (asset != null && EditorUtility.IsPersistent(asset) && EditorUtility.IsDirty(asset) && !deletion.Contains(AssetDatabase.GetAssetPath(asset)))
                    foreach (var dependency in EditorUtility.CollectDependencies(new[] { asset }))
                        if (dependency != null) referenced.Add(AssetDatabase.GetAssetPath(dependency));
            var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(s => s.isLoaded).ToList();
            var live = SceneReferenceScan.Collect(scenes);
            if (live.Uncertainties.Count != 0) referenced.UnionWith(deletion); // Uncertain scenes retain every helper.
            foreach (var obj in live.Objects) if (obj != null) referenced.Add(AssetDatabase.GetAssetPath(obj));
            return referenced;
        }
    }
}
