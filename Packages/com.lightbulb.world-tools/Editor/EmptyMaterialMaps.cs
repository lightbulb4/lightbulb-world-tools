using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools
{
    internal static class EmptyMaterialMaps
    {
        internal const double FuzzyMinimumPercent = 99.99;
        internal sealed class Use
        {
            internal Material Material;
            internal string Property;
            internal string Kind;
            internal Texture Texture;
            internal string EditBlock;
            internal string Label => AssetDatabase.GetAssetPath(Material) + " / " + Material.name + " / " + Property;
        }

        internal sealed class Entry
        {
            internal Texture2D Texture;
            internal string Path;
            internal Hash128 Hash;
            internal uint UpdateCount;
            internal Analysis Analysis;
            internal bool Included = true;
            internal bool Expanded;
            internal readonly List<Use> Uses = new List<Use>();
        }

        internal struct Analysis
        {
            internal Color Value;
            internal long Matching;
            internal long Total;
            internal double Percent => Total == 0 ? 0 : 100.0 * Matching / Total;
            internal bool Qualifies(double minimum) => Total > 0 && Matching >= Math.Ceiling(Total * minimum / 100.0);
        }

        internal sealed class Scan
        {
            internal readonly List<Entry> Entries = new List<Entry>();
            internal readonly List<string> Notes = new List<string>();
            internal int Checked;
        }

        // Shader slot names/descriptions are the authority, rather than unreliable texture filenames.
        internal static string Classify(string name, string description = "")
        {
            string text = Regex.Replace(name + " " + description, "([A-Z]+)([A-Z][a-z])", "$1 $2");
            text = Regex.Replace(text, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant();
            if (text.Contains("metal")) return "Metallic";
            if (text.Contains("rough") || text.Contains("smooth") || text.Contains("gloss")) return "Roughness / smoothness";
            if (text.Contains("occlusion") || Regex.IsMatch(text, @"(^|[^a-z])ao([^a-z]|$)")) return "AO";
            if (text.Contains("normal") || text.Contains("bump")) return "Normal";
            if (text.Contains("height") || text.Contains("parallax") || text.Contains("displacement")) return "Height";
            if (text.Contains("packed") || text.Contains("mask map") || Regex.IsMatch(text, @"(^|[^a-z])(orm|arm|rma|mrao|maskmap)([^a-z]|$)")) return "Packed data";
            return null;
        }

        internal static IEnumerable<Use> Uses(Material material)
        {
            var descriptions = new Dictionary<string, string>();
            if (material.shader != null)
                for (int i = 0; i < ShaderUtil.GetPropertyCount(material.shader); i++)
                    descriptions[ShaderUtil.GetPropertyName(material.shader, i)] = ShaderUtil.GetPropertyDescription(material.shader, i);
            using (var serialized = new SerializedObject(material))
            {
                var slots = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
                if (slots == null) throw new InvalidOperationException("Cannot inspect texture references: " + material.name);
                for (int i = 0; i < slots.arraySize; i++)
                {
                    var slot = slots.GetArrayElementAtIndex(i);
                    string property = slot.FindPropertyRelative("first").stringValue;
                    var texture = slot.FindPropertyRelative("second.m_Texture").objectReferenceValue as Texture;
                    if (texture == null) continue;
                    descriptions.TryGetValue(property, out string description);
                    yield return new Use { Material = material, Property = property, Texture = texture, Kind = Classify(property, description ?? "") };
                }
            }
        }

        internal static Texture ReferencedTexture(Use use) => use.Texture;

        internal static Scan Collect(IEnumerable<Material> materials, double minimum, Func<string, bool> cancel = null)
        {
            ValidateThreshold(minimum);
            var result = new Scan();
            var entries = new Dictionary<Texture2D, Entry>();
            foreach (Material material in materials.Where(m => m != null).Distinct())
            {
                if (cancel != null && cancel(material.name)) throw new OperationCanceledException();
                string editBlock = CannotEdit(material);
                foreach (Use use in Uses(material))
                {
                    use.EditBlock = editBlock;
                    Texture texture = ReferencedTexture(use);
                    if (!(texture is Texture2D image))
                    {
                        if (use.Kind != null) result.Notes.Add("Unsupported texture type: " + use.Label);
                        continue;
                    }
                    if (!entries.TryGetValue(image, out Entry entry))
                        entries[image] = entry = new Entry { Texture = image, Path = AssetDatabase.GetAssetPath(image) };
                    entry.Uses.Add(use);
                }
            }
            using (var reader = new PixelReader())
            {
                foreach (Entry entry in entries.Values.Where(e => e.Uses.Any(u => u.Kind != null)))
                {
                    try
                    {
                        if (string.IsNullOrEmpty(entry.Path)) throw new InvalidOperationException("Texture has no asset path");
                        entry.Analysis = Analyze(() => reader.Read(entry.Texture, cancel), minimum);
                        result.Checked++;
                        if (!entry.Analysis.Qualifies(minimum)) continue;
                        entry.Hash = AssetDatabase.GetAssetDependencyHash(entry.Path);
                        entry.UpdateCount = entry.Texture.updateCount;
                        result.Entries.Add(entry);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result.Notes.Add(entry.Path + ": " + ex.Message); }
                }
            }
            result.Entries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private static void ValidateThreshold(double minimum)
        {
            if (double.IsNaN(minimum) || minimum < 90 || minimum > 100) throw new ArgumentOutOfRangeException(nameof(minimum));
        }

        // Two full passes: majority vote finds the only possible >50% value, then exact counting verifies it.
        // Color.Equals compares components exactly (Color.operator== uses a tolerance).
        internal static Analysis Analyze(Func<IEnumerable<Color[]>> read, double minimum)
        {
            ValidateThreshold(minimum);
            Color candidate = default;
            long votes = 0;
            foreach (Color[] block in read())
                foreach (Color pixel in block)
                {
                    if (votes == 0) candidate = pixel;
                    votes += pixel.Equals(candidate) ? 1 : -1;
                }
            var result = new Analysis { Value = candidate };
            foreach (Color[] block in read())
                foreach (Color pixel in block)
                {
                    result.Total++;
                    if (pixel.Equals(candidate)) result.Matching++;
                }
            return result;
        }

        internal static string CannotEdit(Material material)
        {
            if ((material.hideFlags & HideFlags.NotEditable) != 0) return "Material is marked NotEditable";
            string path = AssetDatabase.GetAssetPath(material);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ||
                !AssetDatabase.IsMainAsset(material)) return "Package, embedded, or transient material; extract/copy to an Assets .mat first";
            if (!AssetDatabase.IsOpenForEdit(material, StatusQueryOptions.ForceUpdate) ||
                (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return "Material is read-only";
            return null;
        }

        // Rebuild references before changing anything, so newly added, removed, or reassigned slots require a new preview.
        internal static int Remove(IReadOnlyList<Entry> entries, IEnumerable<Material> currentMaterials)
        {
            if (!MaterialTextureBatch.IsIdle) throw new InvalidOperationException("Wait until Unity is idle and outside Play Mode.");
            var selected = entries.Where(e => e.Included).ToList();
            var current = currentMaterials.Where(m => m != null).Distinct().SelectMany(Uses).ToList();
            foreach (Entry entry in selected)
            {
                if (entry.Texture == null || entry.Hash != AssetDatabase.GetAssetDependencyHash(entry.Path) || entry.UpdateCount != entry.Texture.updateCount)
                    throw new InvalidOperationException("Texture changed; scan again: " + entry.Path);
                var actual = current.Where(u => ReferencedTexture(u) == entry.Texture).ToList();
                if (actual.Count != entry.Uses.Count || actual.Any(u => !entry.Uses.Any(p => p.Material == u.Material && p.Property == u.Property)))
                    throw new InvalidOperationException("Material references changed; scan again: " + entry.Path);
            }
            var textures = new HashSet<Texture>(selected.Select(e => (Texture)e.Texture));
            var affected = current.Where(u => textures.Contains(ReferencedTexture(u))).GroupBy(u => u.Material).ToList();
            // Refuse partial removal: the preview tells the user which material must be made editable.
            foreach (var group in affected)
            {
                string reason = CannotEdit(group.Key);
                if (reason != null) throw new InvalidOperationException(group.Key.name + ": " + reason);
            }
            if (affected.Count == 0) return 0;
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Remove empty material maps");
            int changed = 0;
            try
            {
                Undo.RegisterCompleteObjectUndo(affected.Select(g => (Object)g.Key).ToArray(), "Remove empty material maps");
                foreach (var group in affected)
                {
                    using (var serialized = new SerializedObject(group.Key))
                    {
                        var slots = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
                        for (int i = 0; i < slots.arraySize; i++)
                        {
                            var value = slots.GetArrayElementAtIndex(i).FindPropertyRelative("second.m_Texture");
                            if (!(value.objectReferenceValue is Texture texture) || !textures.Contains(texture)) continue;
                            value.objectReferenceValue = null;
                            changed++;
                        }
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                    }
                    EditorUtility.SetDirty(group.Key);
                    if (Uses(group.Key).Any(u => textures.Contains(ReferencedTexture(u))))
                        throw new InvalidOperationException("Reference removal did not persist on " + group.Key.name);
                }
                Undo.CollapseUndoOperations(undoGroup);
                return changed;
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
        }

        internal sealed class PixelReader : IDisposable
        {
            private readonly Material copy;
            internal PixelReader()
            {
                Shader shader = Shader.Find("Hidden/Lightbulb/ReadMaterialMap");
                if (shader == null || !shader.isSupported || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
                    throw new InvalidOperationException("Full-resolution texture reading requires a supported graphics device (no -nographics).");
                copy = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            internal IEnumerable<Color[]> Read(Texture2D texture, Func<string, bool> cancel = null)
            {
                if (texture.streamingMipmaps && texture.loadedMipmapLevel != 0)
                    throw new InvalidOperationException("Full-resolution streaming mip is not loaded; disable texture streaming before scanning");
                int rows = Math.Min(128, texture.height);
                var pixels = new Texture2D(texture.width, rows, TextureFormat.RGBAFloat, false, true);
                try
                {
                    for (int y = 0; y < texture.height; y += rows)
                    {
                        if (cancel != null && cancel(texture.name + " (row " + y + "/" + texture.height + ")")) throw new OperationCanceledException();
                        int count = Math.Min(rows, texture.height - y);
                        copy.SetInt("_Row", y);
                        // Exact strip height also handles ReadPixels' bottom-left origin on top-origin graphics APIs.
                        var target = RenderTexture.GetTemporary(texture.width, count, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                        RenderTexture previous = RenderTexture.active;
                        bool srgb = GL.sRGBWrite;
                        try
                        {
                            GL.sRGBWrite = false;
                            Graphics.Blit(texture, target, copy);
                            RenderTexture.active = target;
                            pixels.ReadPixels(new Rect(0, 0, texture.width, count), 0, 0, false);
                            yield return pixels.GetPixels(0, 0, texture.width, count);
                        }
                        finally { RenderTexture.active = previous; GL.sRGBWrite = srgb; RenderTexture.ReleaseTemporary(target); }
                    }
                }
                finally { Object.DestroyImmediate(pixels); }
            }
            public void Dispose() { Object.DestroyImmediate(copy); }
        }
    }
}
