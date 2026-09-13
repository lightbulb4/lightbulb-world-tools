using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools
{
    internal static class MochieScenePacker
    {
        internal sealed class Entry
        {
            internal Material Material;
            internal bool Primary;
            internal bool Detail;
            internal bool CleanupPrimary;
            internal bool CleanupDetail;
            internal bool Included = true;
            internal string State;
            internal readonly Dictionary<string, Hash128> Sources = new Dictionary<string, Hash128>();
        }

        internal sealed class Preview
        {
            internal Scene Scene;
            internal readonly List<Entry> Entries = new List<Entry>();
            internal readonly List<string> Notes = new List<string>();
        }

        internal sealed class Result
        {
            internal int Changed;
            internal int Reused;
            internal int ClearedReferences;
            internal bool Cancelled;
            internal readonly List<string> Outputs = new List<string>();
            internal readonly List<string> Errors = new List<string>();
        }

        private static string[] Maps(bool detail) => detail
            ? new[] { "_DetailOcclusionMap", "_DetailRoughnessMap", "_DetailMetallicMap" }
            : new[] { "_OcclusionMap", "_RoughnessMap", "_MetallicMap", "_HeightMap" };

        internal static Preview Collect(Scene scene, bool includeDetail)
        {
            var preview = new Preview { Scene = scene };
            foreach (Material material in SceneMaterials.Collect(scene))
            {
                if (material.shader == null || (material.shader.name != "Mochie/Standard" && material.shader.name != "Mochie/Standard Lite")) continue;
                string reason = EmptyMaterialMaps.CannotEdit(material);
                if (reason != null) { preview.Notes.Add(material.name + ": " + reason); continue; }
                try
                {
                    bool primary = Eligible(material, false);
                    bool detail = includeDetail && material.shader.name == "Mochie/Standard" && Eligible(material, true);
                    bool cleanupPrimary = CleanupEligible(material, false, preview.Notes);
                    bool cleanupDetail = includeDetail && material.shader.name == "Mochie/Standard" && CleanupEligible(material, true, preview.Notes);
                    if (!primary && !detail && !cleanupPrimary && !cleanupDetail)
                    { preview.Notes.Add(material.name + ": no separate maps to pack or safe leftover references to clear"); continue; }
                    var entry = new Entry { Material = material, Primary = primary, Detail = detail,
                        CleanupPrimary = cleanupPrimary, CleanupDetail = cleanupDetail, State = EditorJsonUtility.ToJson(material) };
                    foreach (bool isDetail in new[] { false, true })
                    {
                        if (isDetail ? !detail : !primary) continue;
                        foreach (string property in Maps(isDetail))
                        {
                            Texture texture = material.GetTexture(property);
                            if (texture == null) continue;
                            string path = AssetDatabase.GetAssetPath(texture);
                            if (!(texture is Texture2D) || string.IsNullOrEmpty(path))
                                throw new InvalidOperationException(property + " requires an asset-backed Texture2D");
                            if (texture is Texture2D image && image.streamingMipmaps && image.loadedMipmapLevel != 0)
                                throw new InvalidOperationException(property + ": full-resolution streaming mip is not loaded");
                            entry.Sources[path] = AssetDatabase.GetAssetDependencyHash(path);
                        }
                    }
                    foreach (bool isDetail in new[] { false, true })
                    {
                        if (isDetail ? !cleanupDetail : !cleanupPrimary) continue;
                        string path = AssetDatabase.GetAssetPath(material.GetTexture(isDetail ? "_DetailPackedMap" : "_PackedMap"));
                        entry.Sources[path] = AssetDatabase.GetAssetDependencyHash(path);
                    }
                    preview.Entries.Add(entry);
                }
                catch (Exception ex) { preview.Notes.Add(material.name + ": " + ex.Message); }
            }
            return preview;
        }

        private static bool Eligible(Material material, bool detail)
        {
            string prefix = detail ? "_Detail" : "_";
            string workflow = detail ? "_DetailWorkflow" : "_PrimaryWorkflow";
            var required = Maps(detail).Concat(new[] { workflow, prefix + "PackedMap", prefix + "OcclusionStrength", prefix + "RoughnessStrength", prefix + "MetallicStrength",
                prefix + "OcclusionChannel", prefix + "RoughnessChannel", prefix + "MetallicChannel" });
            if (!detail) required = required.Concat(new[] { "_HeightStrength", "_HeightChannel", "_PackedHeight", "_PackedMetallicStrength", "_PackedRoughnessStrength", "_PackedOcclusionStrength" });
            foreach (string property in required)
                if (!material.HasProperty(property)) throw new InvalidOperationException("Unsupported Mochie property layout: " + property);
            return material.GetFloat(workflow) == 0 && Maps(detail).Any(p => material.GetTexture(p) != null);
        }

        private static bool CleanupEligible(Material material, bool detail, List<string> notes)
        {
            if (material.GetFloat(detail ? "_DetailWorkflow" : "_PrimaryWorkflow") != 1 || !Maps(detail).Any(p => material.GetTexture(p) != null)) return false;
            try { RequirePacked(material, detail); return true; }
            catch (InvalidOperationException ex) { notes.Add(material.name + ": " + ex.Message); return false; }
        }

        private static void RequirePacked(Material material, bool detail)
        {
            string property = detail ? "_DetailPackedMap" : "_PackedMap";
            if (material.GetFloat(detail ? "_DetailWorkflow" : "_PrimaryWorkflow") != 1 ||
                !material.IsKeywordEnabled(detail ? "_WORKFLOW_DETAIL_PACKED_ON" : "_WORKFLOW_PACKED_ON") ||
                !(material.GetTexture(property) is Texture2D texture) || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(texture)))
                throw new InvalidOperationException(property + ": cleanup requires an assigned texture asset and an enabled packed workflow/keyword; references retained.");
        }

        private static int ClearSources(Material material, bool detail)
        {
            RequirePacked(material, detail);
            int count = 0;
            foreach (string property in Maps(detail))
            {
                if (material.GetTexture(property) == null) continue;
                material.SetTexture(property, null);
                count++;
            }
            return count;
        }

        internal static Result Apply(Preview preview, Adapter adapter, Func<string, bool> cancel = null)
        {
            SceneMaterials.RequireActive(preview.Scene);
            var selected = preview.Entries.Where(e => e.Included).ToList();
            var current = new HashSet<Material>(SceneMaterials.Collect(preview.Scene));
            // Validate the whole selection before creating output files or changing materials.
            foreach (Entry entry in selected)
            {
                if (entry.Material == null || !current.Contains(entry.Material) || EditorJsonUtility.ToJson(entry.Material) != entry.State)
                    throw new InvalidOperationException("Scene/material changed; scan again before packing.");
                string reason = EmptyMaterialMaps.CannotEdit(entry.Material);
                if (reason != null) throw new InvalidOperationException(entry.Material.name + ": " + reason);
                foreach (var source in entry.Sources)
                    if (AssetDatabase.GetAssetDependencyHash(source.Key) != source.Value)
                        throw new InvalidOperationException("Source texture changed; scan again: " + source.Key);
            }
            var result = new Result();
            // Only completed packs are shared, and only for this operation. No persistent cache to become stale.
            var outputs = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            if (selected.Count == 0) return result;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Pack Mochie scene materials");
            Undo.RegisterCompleteObjectUndo(selected.Select(e => (Object)e.Material).ToArray(), "Pack Mochie scene materials");
            try
            {
                foreach (Entry entry in selected)
                {
                    if (cancel != null && cancel(entry.Material.name)) { result.Cancelled = true; break; }
                    SceneMaterials.RequireActive(preview.Scene);
                    try
                    {
                        if (entry.Primary) adapter.Pack(entry.Material, false, outputs, result);
                        if (entry.Detail) adapter.Pack(entry.Material, true, outputs, result);
                        // Both packing operations must succeed before discarding their material references.
                        // Configure has already captured height presence and absent detail-channel strengths.
                        // Existing packed workflows only lose references: never reconfigure their channels,
                        // strengths, UVs, keywords or AreaLit settings from the leftover source maps.
                        int cleared = 0;
                        if (entry.Primary || entry.CleanupPrimary) cleared += ClearSources(entry.Material, false);
                        if (entry.Detail || entry.CleanupDetail) cleared += ClearSources(entry.Material, true);
                        EditorUtility.SetDirty(entry.Material);
                        result.Changed++;
                        result.ClearedReferences += cleared;
                    }
                    catch (Exception ex)
                    {
                        EditorJsonUtility.FromJsonOverwrite(entry.State, entry.Material);
                        EditorUtility.SetDirty(entry.Material);
                        result.Errors.Add(entry.Material.name + ": " + ex.GetBaseException().Message);
                    }
                }
            }
            finally { Undo.CollapseUndoOperations(group); }
            return result;
        }

        // Optional adapter: no Mochie assembly reference, copied packer, or edits to the installed shader.
        internal sealed class Adapter
        {
            private readonly MethodInfo pack;
            private readonly MethodInfo keywords;
            private readonly MethodInfo blend;
            private readonly MethodInfo scaleAndOffset;
            private readonly object editor;

            internal Adapter()
            {
                Type packer = FindType("Mochie.TexturePacker");
                Type editorType = FindType("Mochie.StandardEditor");
                editor = Activator.CreateInstance(editorType);
                var version = editorType.GetField("versionLabel", BindingFlags.Instance | BindingFlags.NonPublic);
                if (version == null || !Equals(version.GetValue(editor), "v2.13"))
                    throw new InvalidOperationException("This tool supports Mochie Standard Editor v2.13. The installed version has not been verified.");
                var signature = new List<Type> { typeof(Material) };
                for (int i = 0; i < 4; i++) { signature.Add(typeof(MaterialProperty)); signature.Add(typeof(float)); }
                signature.Add(typeof(MaterialProperty));
                pack = packer.GetMethod("PackTextures", BindingFlags.Public | BindingFlags.Static, null, signature.ToArray(), null);
                keywords = editorType.GetMethod("SetKeywords", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Material) }, null);
                blend = editorType.GetMethod("SetBlendMode", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Material) }, null);
                scaleAndOffset = packer.GetMethod("GetTextureScaleAndOffset", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(Material), typeof(MaterialProperty), typeof(string) }, null);
                if (pack == null || pack.ReturnType != typeof(Texture2D) || keywords == null || blend == null || scaleAndOffset == null || scaleAndOffset.ReturnType != typeof(Vector4))
                    throw new InvalidOperationException("The installed Mochie packing API is incompatible. No materials changed.");
                Shader shader = Shader.Find("Hidden/Mochie/TexturePacker");
                if (shader == null || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
                    throw new InvalidOperationException("Mochie's texture-packing shader is missing or cannot run on this graphics device.");
            }

            private static Type FindType(string name)
            {
                var types = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).Where(t => t != null).ToArray();
                if (types.Length != 1) throw new InvalidOperationException("Install one copy of Mochie Standard v2.13 first (missing or duplicate " + name + ").");
                return types[0];
            }

            private sealed class Request
            {
                internal object[] Arguments;
                internal string Key;
            }

            private Request BuildRequest(Material material, bool detail)
            {
                if (!Eligible(material, detail)) throw new InvalidOperationException("Material is no longer eligible for packing.");
                var properties = MaterialEditor.GetMaterialProperties(new Object[] { material }).ToDictionary(p => p.name);
                string prefix = detail ? "_Detail" : "_";
                var arguments = new List<object> { material };
                // Encode exact native inputs, not the whole material. In particular, AreaLit settings,
                // runtime height/detail strengths, and material names do not change the baked pixels.
                using (var bytes = new MemoryStream())
                using (var key = new BinaryWriter(bytes))
                {
                    key.Write(detail);
                    foreach (string channel in new[] { "Occlusion", "Roughness", "Metallic", "Height" })
                    {
                        if (detail && channel == "Height") { arguments.Add(null); arguments.Add(1f); continue; }
                        var property = properties[prefix + channel + "Map"];
                        arguments.Add(property);
                        // Height and detail strengths are still applied by the packed shader at runtime.
                        // Passing 1 keeps them from being baked and then applied a second time.
                        float strength = detail || channel == "Height" ? 1f : properties[prefix + channel + "Strength"].floatValue;
                        arguments.Add(strength);
                        key.Write(strength);
                        Vector4 st = (Vector4)scaleAndOffset.Invoke(null, new object[] { material, property, property.name });
                        for (int i = 0; i < 4; i++) key.Write(st[i]);
                        Texture texture = property.textureValue;
                        key.Write(texture != null);
                        if (texture != null)
                        {
                            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out string guid, out long localId))
                                throw new InvalidOperationException("Source texture has no stable asset identity.");
                            key.Write(guid);
                            key.Write(localId);
                            key.Write(AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(texture)).ToString());
                        }
                    }
                    arguments.Add(properties[prefix + "PackedMap"]);
                    key.Flush();
                    return new Request { Arguments = arguments.ToArray(), Key = Convert.ToBase64String(bytes.ToArray()) };
                }
            }

            internal void Pack(Material material, bool detail, Dictionary<string, Texture2D> outputs, Result result)
            {
                Request request = BuildRequest(material, detail);
                if (outputs.TryGetValue(request.Key, out Texture2D shared))
                {
                    Configure(material, detail, shared);
                    result.Reused++;
                    return;
                }
                RenderTexture previous = RenderTexture.active;
                bool srgb = GL.sRGBWrite;
                Texture2D output;
                try { GL.sRGBWrite = false; output = (Texture2D)pack.Invoke(null, request.Arguments); }
                finally { RenderTexture.active = previous; GL.sRGBWrite = srgb; }
                string packedProperty = detail ? "_DetailPackedMap" : "_PackedMap";
                if (output == null || material.GetTexture(packedProperty) != output)
                    throw new InvalidOperationException("Mochie did not assign the packed texture.");
                Configure(material, detail, output);
                outputs.Add(request.Key, output);
                result.Outputs.Add(AssetDatabase.GetAssetPath(output));
            }

            private void Configure(Material material, bool detail, Texture2D output)
            {
                string prefix = detail ? "_Detail" : "_";
                string packedProperty = prefix + "PackedMap";
                string path = output != null ? AssetDatabase.GetAssetPath(output) : "";
                if (output == null || !path.StartsWith("Assets/", StringComparison.Ordinal) || !File.Exists(path))
                    throw new InvalidOperationException("Mochie did not return and assign a saved packed texture.");
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null || importer.sRGBTexture) throw new InvalidOperationException("Packed texture was not imported as linear: " + path);
                material.SetTexture(packedProperty, output);
                material.SetTextureScale(packedProperty, Vector2.one);
                material.SetTextureOffset(packedProperty, Vector2.zero);
                material.SetFloat(detail ? "_DetailWorkflow" : "_PrimaryWorkflow", 1);
                material.SetFloat(prefix + "OcclusionChannel", 0);
                material.SetFloat(prefix + "RoughnessChannel", 1);
                material.SetFloat(prefix + "MetallicChannel", 2);
                if (!detail)
                {
                    material.SetFloat("_HeightChannel", 3);
                    material.SetFloat("_PackedHeight", material.GetTexture("_HeightMap") != null ? 1 : 0);
                    material.SetFloat("_PackedMetallicStrength", 1);
                    material.SetFloat("_PackedRoughnessStrength", 1);
                    material.SetFloat("_PackedOcclusionStrength", 1);
                }
                else
                {
                    // Separate workflow does not blend absent detail maps. Packed workflow samples all channels.
                    foreach (string channel in new[] { "Occlusion", "Roughness", "Metallic" })
                        if (material.GetTexture(prefix + channel + "Map") == null) material.SetFloat(prefix + channel + "Strength", 0);
                }
                keywords.Invoke(editor, new object[] { material });
                blend.Invoke(null, new object[] { material });
                if (!material.IsKeywordEnabled(detail ? "_WORKFLOW_DETAIL_PACKED_ON" : "_WORKFLOW_PACKED_ON"))
                    throw new InvalidOperationException("Mochie did not enable the packed-workflow keyword.");
            }
        }
    }
}
