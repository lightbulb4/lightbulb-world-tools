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
                    if (!primary && !detail) { preview.Notes.Add(material.name + ": already packed, or no separate data maps to pack"); continue; }
                    var entry = new Entry { Material = material, Primary = primary, Detail = detail, State = EditorJsonUtility.ToJson(material) };
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
                        if (entry.Primary) result.Outputs.Add(adapter.Pack(entry.Material, false));
                        if (entry.Detail) result.Outputs.Add(adapter.Pack(entry.Material, true));
                        EditorUtility.SetDirty(entry.Material);
                        result.Changed++;
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
                if (pack == null || pack.ReturnType != typeof(Texture2D) || keywords == null || blend == null)
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

            internal string Pack(Material material, bool detail)
            {
                if (!Eligible(material, detail)) throw new InvalidOperationException("Material is no longer eligible for packing.");
                var properties = MaterialEditor.GetMaterialProperties(new Object[] { material }).ToDictionary(p => p.name);
                string prefix = detail ? "_Detail" : "_";
                var arguments = new List<object> { material };
                foreach (string channel in new[] { "Occlusion", "Roughness", "Metallic", "Height" })
                {
                    if (detail && channel == "Height") { arguments.Add(null); arguments.Add(1f); continue; }
                    arguments.Add(properties[prefix + channel + "Map"]);
                    // Height and detail strengths are still applied by the packed shader at runtime.
                    // Passing 1 keeps them from being baked and then applied a second time.
                    arguments.Add(detail || channel == "Height" ? 1f : properties[prefix + channel + "Strength"].floatValue);
                }
                string packedProperty = prefix + "PackedMap";
                arguments.Add(properties[packedProperty]);
                RenderTexture previous = RenderTexture.active;
                bool srgb = GL.sRGBWrite;
                Texture2D output;
                try { GL.sRGBWrite = false; output = (Texture2D)pack.Invoke(null, arguments.ToArray()); }
                finally { RenderTexture.active = previous; GL.sRGBWrite = srgb; }
                string path = output != null ? AssetDatabase.GetAssetPath(output) : "";
                if (output == null || material.GetTexture(packedProperty) != output || !path.StartsWith("Assets/", StringComparison.Ordinal) || !File.Exists(path))
                    throw new InvalidOperationException("Mochie did not return and assign a saved packed texture.");
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null || importer.sRGBTexture) throw new InvalidOperationException("Packed texture was not imported as linear: " + path);
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
                return path;
            }
        }
    }
}
