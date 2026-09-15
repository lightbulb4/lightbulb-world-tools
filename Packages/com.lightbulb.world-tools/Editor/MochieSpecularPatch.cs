using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Lightbulb.WorldTools
{
    // Deliberately version-locked: an unknown upstream change needs review, not a fuzzy replacement.
    internal static class MochieSpecularPatch
    {
        private static readonly System.Reflection.FieldInfo BakeryBake = LightingExperimentAdapter.Find("ftRenderLightmap")?.GetField(
            "bakeInProgress", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        internal static bool IsIdle => MaterialTextureBatch.IsIdle && !Lightmapping.isRunning &&
            !(BakeryBake != null && BakeryBake.FieldType == typeof(bool) && (bool)BakeryBake.GetValue(null));
        internal const string Marker = "// Lightbulb Dominant Direction specular v1";
        internal const string Anchor = "                    indirectCol = DecodeDirectionalLightmap(indirectCol, lightmapDir, id.normal, 1.5);";
        internal const string LightingHash = "cde6fd3062a4c4b0a907183e5522ff2fd5b76118e992282d7094fe08fad5d16c";
        internal const string BrdfHash = "b6085b4d28a2b0d3a3d89ac0fb9a2cccacba4c7f3f78e1cfce0f7b034d21ae93";
        // Uses already sampled data and Mochie's existing lmSpec tint/strength/occlusion processing.
        // View direction here points TO the camera, unlike Bakery's surface-shader eyeVec.
        internal const string Addition = @"
                    // Lightbulb Dominant Direction specular v1
                    #if defined(BAKERY_LMSPEC) && defined(LIGHTMAP_ON) && !defined(STANDARD_MOBILE)
                        float3 lbDirection = lightmapDir.xyz * 2.0 - 1.0;
                        float lbDirectionSq = dot(lbDirection, lbDirection);
                        if (lbDirectionSq > 1e-6) {
                            float3 lbHalf = lbDirection * rsqrt(lbDirectionSq) + viewDir;
                            float lbHalfSq = dot(lbHalf, lbHalf);
                            if (lbHalfSq > 1e-6) {
                                float lbNoH = saturate(dot(id.normal, lbHalf * rsqrt(lbHalfSq)));
                                // Clamp the BRDF roughness, not the material. Avoid singular mirror highlights.
                                float lbRoughness = max(bakeryLMSpecRough, 0.002);
                                lmSpec = max(indirectCol, 0.0) * GGXTerm(lbNoH, lbRoughness);
                            }
                        }
                    #endif
                    // End Lightbulb Dominant Direction specular v1";

        internal sealed class Inspection
        {
            internal string Path;
            internal string Message;
            internal bool Compatible;
            internal bool Patched;
        }

        internal static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))))
                    .Replace("-", "").ToLowerInvariant();
        }

        internal static string Transform(string text, bool install)
        {
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string addition = Addition.Replace("\r\n", "\n").Replace("\n", newline);
            bool marked = text.Contains(Marker);
            if (marked && (Count(text, addition) != 1 || Count(text, Marker) != 1))
                throw new InvalidOperationException("The existing patch was edited or duplicated. No source was changed.");
            string original = marked ? text.Replace(addition, "") : text;
            // Removal only removes our exact block, preserving subsequent unrelated user edits.
            if (!install) return original;
            if (Hash(original) != LightingHash || Count(original, Anchor) != 1)
                throw new InvalidOperationException("Unrecognized Mochie lighting source (possibly a newer version or native implementation). Review required; no patch applied.");
            return marked ? text : original.Replace(Anchor, Anchor + addition);
        }

        private static int Count(string text, string value) => (text.Length - text.Replace(value, "").Length) / value.Length;

        internal static string LightingPath(Shader shader)
        {
            string shaderPath = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(shaderPath)) throw new InvalidOperationException("Mochie Standard / Standard Lite is not installed.");
            string directory = System.IO.Path.GetDirectoryName(shaderPath).Replace('\\', '/');
            return directory + "/StandardLighting.cginc";
        }

        internal static Inspection Inspect(string path = null)
        {
            var result = new Inspection { Path = path };
            try
            {
                result.Path = path ?? LightingPath(Shader.Find("Mochie/Standard") ?? Shader.Find("Mochie/Standard Lite"));
                string source = File.ReadAllText(result.Path);
                result.Patched = source.Contains(Marker);
                Transform(source, true);
                string brdf = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(result.Path), "StandardBRDF.cginc");
                if (Hash(File.ReadAllText(brdf)) != BrdfHash)
                    throw new InvalidOperationException("Unrecognized Mochie BRDF source. Review required before enabling this patch.");
                result.Compatible = true;
                result.Message = result.Patched ? "Compatible patch installed." : "Compatible Mochie source found; patch not installed.";
            }
            catch (Exception ex) { result.Message = ex.Message; }
            return result;
        }

        internal static string SetInstalled(string path, bool install)
        {
            if (!IsIdle)
                throw new InvalidOperationException("Wait until Unity is idle, outside Play Mode, builds and baking.");
            string fullPath = System.IO.Path.GetFullPath(path);
            string assetsRoot = System.IO.Path.GetFullPath(Application.dataPath) + System.IO.Path.DirectorySeparatorChar;
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !fullPath.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetFileName(path) != "StandardLighting.cginc")
                throw new InvalidOperationException("Only an installed, writable Assets/.../StandardLighting.cginc can be patched. Package caches are not modified.");
            if (!AssetDatabase.IsOpenForEdit(path, StatusQueryOptions.ForceUpdate) || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new InvalidOperationException("The Mochie source is read-only.");
            var inspection = Inspect(path);
            if (install && !inspection.Compatible) throw new InvalidOperationException(inspection.Message);
            byte[] before = File.ReadAllBytes(path);
            bool bom = before.Length >= 3 && before[0] == 239 && before[1] == 187 && before[2] == 191;
            var encoding = new UTF8Encoding(bom, true);
            string text = encoding.GetString(before, bom ? 3 : 0, before.Length - (bom ? 3 : 0));
            string after = Transform(text, install);
            if (after == text) return "No source change needed.";
            string backup = "Library/LightbulbWorldTools/MochieSpecular/" + Guid.NewGuid().ToString("N") + "/StandardLighting.cginc";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup));
            File.WriteAllBytes(backup, before);
            // Refuse to overwrite a concurrent import/edit.
            if (!before.SequenceEqual(File.ReadAllBytes(path))) throw new InvalidOperationException("Mochie changed during the operation. Try again.");
            File.WriteAllText(path, after, encoding);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return (install ? "Patch installed. " : "Patch removed. ") + "Backup: " + backup;
        }
    }

    [FilePath("ProjectSettings/LightbulbMochieSpecular.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class MochieSpecularPatchSettings : ScriptableSingleton<MochieSpecularPatchSettings>
    {
        [SerializeField] internal bool autoReapply;
        [SerializeField] internal string lightingGuid;
        internal void Configure(string path, bool enabled)
        {
            if (!string.IsNullOrEmpty(path)) lightingGuid = AssetDatabase.AssetPathToGUID(path);
            autoReapply = enabled;
            Save(true);
        }
    }

    [InitializeOnLoad]
    internal sealed class MochieSpecularPatchWatcher : AssetPostprocessor
    {
        private static bool queued;
        private static string lastWarning;
        static MochieSpecularPatchWatcher() { Queue(); }
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(p => p.EndsWith("StandardLighting.cginc", StringComparison.Ordinal) ||
                p.EndsWith("StandardBRDF.cginc", StringComparison.Ordinal))) Queue();
        }
        private static void Queue()
        {
            if (queued) return;
            queued = true;
            EditorApplication.delayCall += Check;
        }
        private static void Check()
        {
            queued = false;
            var settings = MochieSpecularPatchSettings.instance;
            if (!settings.autoReapply || Application.isBatchMode) return;
            if (!MochieSpecularPatch.IsIdle) { Queue(); return; }
            string path = AssetDatabase.GUIDToAssetPath(settings.lightingGuid);
            var state = MochieSpecularPatch.Inspect(path);
            if (!state.Compatible) { Warn(state.Message); return; }
            if (state.Patched) { lastWarning = null; return; }
            try { Debug.Log("[Lightbulb] " + MochieSpecularPatch.SetInstalled(path, true)); lastWarning = null; }
            catch (Exception ex) { Warn(ex.Message); }
        }
        private static void Warn(string message)
        {
            if (lastWarning == message) return;
            lastWarning = message;
            Debug.LogWarning("[Lightbulb Mochie specular] " + message);
        }
    }

    // Never rewrite shaders during a build. An opted-in project must not silently ship an overwritten patch.
    internal sealed class MochieSpecularBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        public void OnPreprocessBuild(BuildReport report)
        {
            var settings = MochieSpecularPatchSettings.instance;
            if (!settings.autoReapply) return;
            var state = MochieSpecularPatch.Inspect(AssetDatabase.GUIDToAssetPath(settings.lightingGuid));
            if (!state.Compatible || !state.Patched)
                throw new BuildFailedException("Lightbulb Mochie specular: " + state.Message + " Open Tools/Lightbulb/Mochie Baked Specular before building.");
        }
    }
}
