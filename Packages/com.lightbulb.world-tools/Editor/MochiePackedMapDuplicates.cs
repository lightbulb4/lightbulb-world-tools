using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools
{
    internal static class MochiePackedMapDuplicates
    {
        internal sealed class Slot
        {
            internal Material Material;
            internal string Property;
            internal Texture2D Texture;
            internal string State;
        }

        internal sealed class Group
        {
            internal Texture2D Keep;
            internal readonly List<Slot> Replacements = new List<Slot>();
            internal bool Included = true;
        }

        internal sealed class Preview
        {
            internal Scene Scene;
            internal readonly List<Group> Groups = new List<Group>();
            internal readonly List<string> Notes = new List<string>();
            internal readonly Dictionary<Texture2D, Snapshot> Textures = new Dictionary<Texture2D, Snapshot>();
        }

        internal sealed class Snapshot
        {
            internal string Path;
            internal string Content;
            internal string Settings;
            internal Hash128 Dependency;

            internal bool SameImportAndContent(Snapshot other) => Content == other.Content && Settings == other.Settings;
        }

        // Comparing every serialized importer setting also covers inactive platform overrides. Keep unknown
        // fields: false negatives are preferable to substituting a texture with different import behavior.
        private static Snapshot Capture(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                importer == null || importer.textureType != TextureImporterType.Default || importer.textureShape != TextureImporterShape.Texture2D)
                throw new InvalidOperationException("Only PNG maps with a Default / 2D texture importer under Assets are compared.");
            if (EditorUtility.IsDirty(importer)) throw new InvalidOperationException("Apply or revert pending texture import settings, then scan again.");
            string metadata = File.ReadAllText(path + ".meta").Replace("\r\n", "\n");
            int start = metadata.IndexOf("\nTextureImporter:\n", StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("Unrecognized texture importer metadata.");
            string content;
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create()) content = Convert.ToBase64String(hash.ComputeHash(stream));
            return new Snapshot { Path = path, Content = content, Settings = metadata.Substring(start),
                Dependency = AssetDatabase.GetAssetDependencyHash(path) };
        }

        private static bool SameBytes(string left, string right)
        {
            using (var a = File.OpenRead(left))
            using (var b = File.OpenRead(right))
            {
                if (a.Length != b.Length) return false;
                var x = new byte[65536];
                var y = new byte[x.Length];
                int count;
                while ((count = a.Read(x, 0, x.Length)) > 0)
                {
                    int read = 0;
                    while (read < count)
                    {
                        int next = b.Read(y, read, count - read);
                        if (next == 0) return false;
                        read += next;
                    }
                    for (int i = 0; i < count; i++) if (x[i] != y[i]) return false;
                }
                return true;
            }
        }

        internal static Preview Collect(Scene scene, Func<string, bool> cancel = null)
        {
            SceneMaterials.RequireActive(scene);
            var preview = new Preview { Scene = scene };
            var slots = new List<Slot>();
            foreach (Material material in SceneMaterials.Collect(scene, cancel))
            {
                string shader = material.shader != null ? material.shader.name : "";
                if (shader != "Mochie/Standard" && shader != "Mochie/Standard Lite") continue;
                string reason = EmptyMaterialMaps.CannotEdit(material);
                if (reason != null) { preview.Notes.Add(material.name + ": " + reason); continue; }
                foreach (string prefix in shader == "Mochie/Standard" ? new[] { "_", "_Detail" } : new[] { "_" })
                {
                    string workflow = prefix == "_" ? "_PrimaryWorkflow" : "_DetailWorkflow";
                    string property = prefix + "PackedMap";
                    if (!material.HasProperty(workflow) || material.GetFloat(workflow) != 1 || !material.HasProperty(property)) continue;
                    if (!(material.GetTexture(property) is Texture2D texture)) continue;
                    slots.Add(new Slot { Material = material, Property = property, Texture = texture, State = EditorJsonUtility.ToJson(material) });
                }
            }
            foreach (Texture2D texture in slots.Select(s => s.Texture).Distinct().OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal))
            {
                if (cancel != null && cancel(texture.name)) throw new OperationCanceledException();
                try { preview.Textures.Add(texture, Capture(texture)); }
                catch (Exception ex) { preview.Notes.Add(AssetDatabase.GetAssetPath(texture) + ": " + ex.Message); }
            }
            // The first path in ordinal order is the stable keeper. Hashes only shortlist candidates;
            // compare the complete files before offering any replacement.
            var candidates = new Dictionary<string, List<Group>>(StringComparer.Ordinal);
            foreach (var item in preview.Textures.OrderBy(p => p.Value.Path, StringComparer.Ordinal))
            {
                string key = item.Value.Content + "\n" + item.Value.Settings;
                if (!candidates.TryGetValue(key, out var bucket)) candidates.Add(key, bucket = new List<Group>());
                Group group = bucket.FirstOrDefault(g => SameBytes(preview.Textures[g.Keep].Path, item.Value.Path));
                if (group == null) bucket.Add(new Group { Keep = item.Key });
                else group.Replacements.AddRange(slots.Where(s => s.Texture == item.Key));
            }
            preview.Groups.AddRange(candidates.Values.SelectMany(b => b).Where(g => g.Replacements.Count > 0)
                .OrderBy(g => preview.Textures[g.Keep].Path, StringComparer.Ordinal));
            return preview;
        }

        internal static int Apply(Preview preview)
        {
            SceneMaterials.RequireActive(preview.Scene);
            var groups = preview.Groups.Where(g => g.Included).ToList();
            var slots = groups.SelectMany(g => g.Replacements).ToList();
            var current = new HashSet<Material>(SceneMaterials.Collect(preview.Scene));
            foreach (Slot slot in slots)
            {
                if (slot.Material == null || !current.Contains(slot.Material) || EditorJsonUtility.ToJson(slot.Material) != slot.State)
                    throw new InvalidOperationException("Scene/material changed; scan again before consolidating.");
                string reason = EmptyMaterialMaps.CannotEdit(slot.Material);
                if (reason != null) throw new InvalidOperationException(slot.Material.name + ": " + reason);
            }
            foreach (Texture2D texture in groups.Select(g => g.Keep).Concat(slots.Select(s => s.Texture)).Distinct())
            {
                if (texture == null) throw new InvalidOperationException("A texture was removed. Scan again.");
                Snapshot before = preview.Textures[texture];
                Snapshot now = Capture(texture);
                if (now.Path != before.Path || now.Dependency != before.Dependency || !now.SameImportAndContent(before))
                    throw new InvalidOperationException("Texture content/import settings changed; scan again: " + before.Path);
            }
            foreach (Group group in groups)
                foreach (Texture2D texture in group.Replacements.Select(s => s.Texture).Distinct())
                    if (!SameBytes(preview.Textures[group.Keep].Path, preview.Textures[texture].Path))
                        throw new InvalidOperationException("Texture files no longer match. Scan again.");
            if (slots.Count == 0) return 0;
            var materials = slots.GroupBy(s => s.Material).ToDictionary(g => g.Key, g => g.First().State);
            Undo.IncrementCurrentGroup();
            int undo = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Consolidate Mochie packed maps");
            Undo.RegisterCompleteObjectUndo(materials.Keys.Cast<Object>().ToArray(), "Consolidate Mochie packed maps");
            try
            {
                foreach (Group group in groups)
                    foreach (Slot slot in group.Replacements)
                    {
                        // Only the reference changes. Material identity, AreaLit settings, channel controls,
                        // keywords and packed-map tiling/offset all remain exactly as they were.
                        slot.Material.SetTexture(slot.Property, group.Keep);
                        EditorUtility.SetDirty(slot.Material);
                    }
            }
            catch
            {
                foreach (var material in materials)
                {
                    EditorJsonUtility.FromJsonOverwrite(material.Value, material.Key);
                    EditorUtility.SetDirty(material.Key);
                }
                throw;
            }
            finally { Undo.CollapseUndoOperations(undo); }
            return slots.Count;
        }
    }
}
