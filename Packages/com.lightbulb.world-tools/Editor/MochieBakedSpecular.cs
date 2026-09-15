using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.WorldTools
{
    internal static class MochieBakedSpecular
    {
        internal sealed class Entry
        {
            internal Material Material;
            internal string State;
            internal string Reason;
            internal bool Recommended;
            internal bool Included;
        }
        internal sealed class Preview
        {
            internal Scene Scene;
            internal readonly List<Entry> Entries = new List<Entry>();
            internal readonly List<string> Notes = new List<string>();
            internal string Receivers;
        }

        internal static bool Supported(Material m) => m != null && m.shader != null &&
            (m.shader.name == "Mochie/Standard" || m.shader.name == "Mochie/Standard Lite");

        // A scalar cannot describe packed/textured/detail/animated roughness. Do not guess from a texture multiplier.
        internal static bool Recommend(Material material, out string reason)
        {
            if (material.GetFloat("_BlendMode") >= 2)
            { reason = "Review: transparent material; baked highlights may not suit this surface."; return false; }
            if (material.GetFloat("_SpecularHighlightsToggle") == 0)
            { reason = "Review: regular highlights were explicitly disabled. That setting will be preserved."; return false; }
            if (material.GetFloat("_PrimaryWorkflow") != 0 || material.GetFloat("_SampleRoughness") != 0 ||
                material.IsKeywordEnabled("_DETAIL_ROUGHNESS_ON") || material.IsKeywordEnabled("_WORKFLOW_DETAIL_PACKED_ON") ||
                material.GetFloat("_RainMode") != 0)
            { reason = "Review: textured, packed, detail or rain-driven roughness; the scalar slider is not the surface roughness."; return false; }
            float strength = material.GetFloat("_RoughnessStrength");
            float roughness = Mathf.Abs(material.GetFloat("_SmoothnessToggle") - strength);
            if (!float.IsFinite(roughness) || roughness > 1)
            { reason = "Review: roughness is outside the usual 0–1 range."; return false; }
            if (roughness < 0.1f)
            { reason = $"Review: very smooth (roughness {roughness:0.###}); reflective, but approximate baked highlights may look harsh."; return false; }
            if (roughness >= 0.9f)
            { reason = $"Review: very rough ({roughness:0.###}); highlights may be subtle. Metallic surfaces can still benefit."; return false; }
            reason = $"Recommended: scalar roughness {roughness:0.###}. Metallic is not required; nonmetals reflect light too.";
            return true;
        }

        private sealed class Use
        {
            internal Renderer Renderer;
            internal Material Material;
            internal LightmapData Map;
        }

        private static List<Use> Uses(Scene scene)
        {
            var uses = new List<Use>();
            var maps = LightmapSettings.lightmaps;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    int index = renderer.lightmapIndex;
                    LightmapData map = index >= 0 && index < maps.Length ? maps[index] : null;
                    foreach (var material in renderer.sharedMaterials.Where(Supported).Distinct())
                        uses.Add(new Use { Renderer = renderer, Material = material, Map = map });
                }
            return uses;
        }

        private static string Fingerprint(IEnumerable<Use> uses) => ((int)LightmapSettings.lightmapsMode) + "|" + string.Join(";", uses.Select(u =>
            u.Renderer.GetInstanceID() + ":" + u.Material.GetInstanceID() + ":" + u.Renderer.lightmapIndex + ":" +
            (u.Map?.lightmapColor != null ? u.Map.lightmapColor.GetInstanceID() : 0) + ":" +
            (u.Map?.lightmapDir != null ? u.Map.lightmapDir.GetInstanceID() : 0)));

        internal static Preview Collect(Scene scene)
        {
            SceneMaterials.RequireActive(scene);
            if (!MochieSpecularPatch.IsIdle) throw new InvalidOperationException("Wait for baking/imports to finish.");
            var uses = Uses(scene);
            var preview = new Preview { Scene = scene, Receivers = Fingerprint(uses) };
            foreach (var group in uses.GroupBy(u => u.Material))
            {
                Material material = group.Key;
                string block = Block(material, group.Any(u => u.Map?.lightmapColor != null),
                    group.Any(u => u.Map?.lightmapColor != null && u.Map.lightmapDir != null));
                if (block != null) { preview.Notes.Add(material.name + ": " + block); continue; }
                bool recommended = Recommend(material, out string reason);
                if (group.Any(u => u.Map?.lightmapColor == null))
                { recommended = false; reason = "Review: shared by lightmapped and non-lightmapped renderers. " + reason; }
                preview.Entries.Add(new Entry { Material = material, State = EditorJsonUtility.ToJson(material),
                    Reason = reason, Recommended = recommended, Included = recommended });
            }
            preview.Entries.Sort((a, b) => string.Compare(a.Material.name, b.Material.name, StringComparison.Ordinal));
            return preview;
        }

        internal static string Block(Material material, bool hasColor, bool hasDirection)
        {
            if (!Supported(material)) return "Unsupported shader (only Mochie Standard and Standard Lite).";
            string cannotEdit = EmptyMaterialMaps.CannotEdit(material);
            if (cannotEdit != null) return cannotEdit;
            string[] required = { "_BAKERY_LMSPEC", "_BakeryLMSpecStrength", "_BakeryMode", "_BlendMode", "_PrimaryWorkflow",
                "_SampleRoughness", "_RoughnessStrength", "_SmoothnessToggle", "_SpecularHighlightsToggle", "_RainMode" };
            if (required.Any(p => !material.HasProperty(p))) return "Unrecognized Mochie material properties.";
            if (material.GetFloat("_BAKERY_LMSPEC") == 1 && material.IsKeywordEnabled("BAKERY_LMSPEC")) return "Already enabled.";
            float strength = material.GetFloat("_BakeryLMSpecStrength");
            if (!float.IsFinite(strength) || strength <= 0) return "Baked-specular strength is zero/invalid; preserved rather than overriding your choice.";
            if (!hasColor) return "No baked lightmap assigned to this material's scene renderers.";
            float mode = material.GetFloat("_BakeryMode");
            if (material.IsKeywordEnabled("BAKERY_SH") != (mode == 1) ||
                material.IsKeywordEnabled("BAKERY_RNM") != (mode == 2) ||
                material.IsKeywordEnabled("BAKERY_MONOSH") != (mode == 3))
                return "Bakery mode and shader keywords disagree; open the material inspector to synchronize, then scan again.";
            if (mode == 0 || mode == 3)
            {
                if (!hasDirection || LightmapSettings.lightmapsMode != LightmapsMode.CombinedDirectional)
                    return "No active directional lightmap data; a material checkbox cannot create it.";
                if (mode == 0)
                {
                    var patch = MochieSpecularPatch.Inspect(MochieSpecularPatch.LightingPath(material.shader));
                    if (!patch.Compatible || !patch.Patched) return "Dominant Direction requires the compatible shader patch first. " + patch.Message;
                }
            }
            else if (mode == 1 || mode == 2)
            {
                if (new[] { "_RNM0", "_RNM1", "_RNM2" }.Any(p => !material.HasProperty(p) || material.GetTexture(p) == null))
                    return "SH/RNM directional textures are missing.";
            }
            else return "Unrecognized Bakery mode.";
            return null;
        }

        internal static int Apply(Preview preview)
        {
            SceneMaterials.RequireActive(preview.Scene);
            if (!MochieSpecularPatch.IsIdle) throw new InvalidOperationException("Wait for baking/imports to finish.");
            if (preview.Receivers != Fingerprint(Uses(preview.Scene)))
                throw new InvalidOperationException("Renderer/lightmap assignments changed. Scan again.");
            var selected = preview.Entries.Where(e => e.Included).ToArray();
            // Validate the complete batch before making the first mutation.
            var fresh = Collect(preview.Scene);
            foreach (var entry in selected)
                if (entry.Material == null || entry.State != EditorJsonUtility.ToJson(entry.Material) ||
                    !fresh.Entries.Any(e => e.Material == entry.Material))
                    throw new InvalidOperationException("Materials or patch compatibility changed. Scan again.");
            if (selected.Length == 0) return 0;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Enable Mochie baked specular");
            try
            {
                Undo.RegisterCompleteObjectUndo(selected.Select(e => (UnityEngine.Object)e.Material).ToArray(), "Enable Mochie baked specular");
                foreach (var entry in selected)
                {
                    entry.Material.SetFloat("_BAKERY_LMSPEC", 1);
                    entry.Material.EnableKeyword("BAKERY_LMSPEC");
                    EditorUtility.SetDirty(entry.Material);
                }
                Undo.CollapseUndoOperations(group);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
            SceneView.RepaintAll();
            return selected.Length;
        }
    }
}
