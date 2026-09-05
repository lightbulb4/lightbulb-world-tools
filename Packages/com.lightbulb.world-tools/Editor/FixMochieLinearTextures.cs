using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using PackageSource = UnityEditor.PackageManager.PackageSource;

namespace Lightbulb.WorldTools
{
    internal static class FixMochieLinearTextures
    {
        private const string MenuPath = "Tools/Lightbulb/Fix Mochie Linear Textures in Scene";
        private const string LogPrefix = "[Lightbulb] ";

        internal sealed class Candidate
        {
            internal string Path;
            internal Texture Texture;
            internal readonly List<string> Uses = new List<string>();
        }

        [MenuItem(MenuPath)]
        private static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer ||
                PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                Debug.LogWarning(LogPrefix + "Run this command in a scene, outside Play Mode, while Unity is idle.");
                return;
            }

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogWarning(LogPrefix + "There is no loaded active scene to scan.");
                return;
            }

            List<Candidate> candidates = Collect(scene);
            foreach (Candidate candidate in candidates)
                Debug.Log(LogPrefix + "Linear texture candidate: " + candidate.Path + "\n" +
                    string.Join("\n", candidate.Uses), candidate.Texture);

            if (candidates.Count == 0)
            {
                Debug.Log(LogPrefix + "No editable Mochie Standard / Standard Lite textures need the linear fix in the active scene.");
                return;
            }

            if (!EditorUtility.DisplayDialog("Fix Mochie Linear Textures",
                $"Set {candidates.Count} unique texture(s) used in '{scene.name}' to linear and reimport them?\n\n" +
                "Only Mochie Standard and Standard Lite warning slots are scanned. Shared textures change everywhere they are used.\n\n" +
                "The Console lists the textures. Original .meta files will be backed up; this operation does not use Unity Undo.",
                "Fix Textures", "Cancel"))
                return;

            Apply(candidates);
        }

        internal static List<Candidate> Collect(Scene scene)
        {
            var materials = new HashSet<Material>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                    foreach (Material material in renderer.sharedMaterials)
                        if (material != null) materials.Add(material);
                foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                    if (terrain.materialTemplate != null) materials.Add(terrain.materialTemplate);
            }

            var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
            var skipped = new HashSet<string>(StringComparer.Ordinal);
            foreach (Material material in materials)
            {
                foreach (string property in WarningSlots(material))
                {
                    if (!material.HasProperty(property)) continue;
                    Texture texture = material.GetTexture(property);
                    if (texture == null) continue;
                    string path = AssetDatabase.GetAssetPath(texture);
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null)
                    {
                        if (skipped.Add(path + texture.GetInstanceID()))
                            Debug.LogWarning(LogPrefix + "Skipped texture without a texture importer: " + texture.name, texture);
                        continue;
                    }
                    if (!importer.sRGBTexture) continue;
                    if (!CanEdit(path, out string reason))
                    {
                        if (skipped.Add(path))
                            Debug.LogWarning(LogPrefix + "Skipped " + path + ": " + reason, texture);
                        continue;
                    }
                    if (!candidates.TryGetValue(path, out Candidate candidate))
                    {
                        candidate = new Candidate { Path = path, Texture = texture };
                        candidates.Add(path, candidate);
                    }
                    candidate.Uses.Add(material.name + " / " + property);
                }
            }
            return candidates.Values.OrderBy(candidate => candidate.Path, StringComparer.Ordinal).ToList();
        }

        // Matches StandardEditor v2.13's sRGBWarning calls, independently of foldout UI state.
        // Lite has the detail properties but DoDetailTexturesLite does not expose these warnings.
        internal static IEnumerable<string> WarningSlots(Material material)
        {
            if (material == null || material.shader == null) yield break;
            string shaderName = material.shader.name;
            if (shaderName != "Mochie/Standard" && shaderName != "Mochie/Standard Lite") yield break;
            if (!material.HasProperty("_PrimaryWorkflow") || !material.HasProperty("_PrimarySampleMode"))
            {
                Debug.LogWarning(LogPrefix + "Unsupported Mochie property layout: " + material.name, material);
                yield break;
            }

            if (material.GetFloat("_PrimaryWorkflow") == 0)
            {
                yield return "_MetallicMap";
                yield return "_RoughnessMap";
                yield return "_OcclusionMap";
                if (material.GetFloat("_PrimarySampleMode") != 3)
                    yield return "_HeightMap";
            }
            else
            {
                yield return "_PackedMap";
            }

            if (shaderName != "Mochie/Standard") yield break;
            if (!material.HasProperty("_DetailWorkflow"))
            {
                Debug.LogWarning(LogPrefix + "Unsupported Mochie detail property layout: " + material.name, material);
                yield break;
            }
            if (material.GetFloat("_DetailWorkflow") == 0)
            {
                yield return "_DetailMetallicMap";
                yield return "_DetailRoughnessMap";
                yield return "_DetailOcclusionMap";
            }
            else
            {
                yield return "_DetailPackedMap";
            }
        }

        private static bool CanEdit(string path, out string reason)
        {
            reason = null;
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                PackageInfo package = PackageInfo.FindForAssetPath(path);
                if (package == null || package.source != PackageSource.Embedded)
                {
                    reason = "Only project assets and embedded package textures can be changed.";
                    return false;
                }
            }
            string metaPath = path + ".meta";
            if (!File.Exists(metaPath) || (File.GetAttributes(metaPath) & FileAttributes.ReadOnly) != 0)
            {
                reason = "The texture's .meta file is missing or read-only.";
                return false;
            }
            return AssetDatabase.IsOpenForEdit(metaPath, out reason, StatusQueryOptions.ForceUpdate);
        }

        internal static int Apply(List<Candidate> candidates)
        {
            string backupRoot = Path.GetFullPath(Path.Combine("Library", "LightbulbWorldTools", "Backups",
                "MochieLinearTextures", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
            int fixedCount = 0;
            int failedCount = 0;
            try
            {
                // Prepare every backup before changing any importer. Relative paths preserve the asset layout.
                foreach (Candidate candidate in candidates)
                {
                    if (!CanEdit(candidate.Path, out string reason))
                        throw new IOException(candidate.Path + ": " + reason);
                    string backup = Path.Combine(backupRoot, candidate.Path + ".meta");
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    File.Copy(candidate.Path + ".meta", backup, false);
                }
                if (candidates.Count == 0) return 0;
                Debug.Log(LogPrefix + "Original texture metadata backups: " + backupRoot +
                    "\nTo roll back, close Unity and copy the backed-up .meta files to their matching project paths. " +
                    "This also restores other import settings to their pre-fix values. Deleting Library removes these backups.");

                foreach (Candidate candidate in candidates)
                {
                    try
                    {
                        var importer = AssetImporter.GetAtPath(candidate.Path) as TextureImporter;
                        if (importer == null) throw new InvalidOperationException("The texture importer is no longer available.");
                        if (!importer.sRGBTexture) continue;
                        importer.sRGBTexture = false;
                        importer.SaveAndReimport();
                        importer = AssetImporter.GetAtPath(candidate.Path) as TextureImporter;
                        if (importer == null || importer.sRGBTexture)
                            throw new InvalidOperationException("The texture did not stay linear after reimport. Check asset postprocessors.");
                        fixedCount++;
                        Debug.Log(LogPrefix + "Fixed linear texture: " + candidate.Path, candidate.Texture);
                    }
                    catch (Exception exception)
                    {
                        failedCount++;
                        Debug.LogError(LogPrefix + "Could not finish fixing " + candidate.Path + ": " + exception.Message +
                            "\nOriginal metadata is in " + backupRoot, candidate.Texture);
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogError(LogPrefix + "Could not prepare texture backups. No textures were changed: " + exception.Message);
                return 0;
            }
            Debug.Log(LogPrefix + $"Mochie linear texture fix finished: {fixedCount} fixed, {failedCount} failed.");
            return fixedCount;
        }
    }
}
