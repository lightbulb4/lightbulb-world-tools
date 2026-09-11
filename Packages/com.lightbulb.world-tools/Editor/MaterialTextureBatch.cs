using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using PackageSource = UnityEditor.PackageManager.PackageSource;

[assembly: InternalsVisibleTo("Lightbulb.WorldTools.Editor.Tests")]

namespace Lightbulb.WorldTools
{
    internal static class MaterialTextureBatch
    {
        internal enum CrunchMode { LeaveUnchanged, Enable, Disable }

        internal sealed class Entry
        {
            internal string Path;
            internal string Guid;
            internal Texture Texture;
            internal string ImporterState;
            internal bool Included = true;
            internal readonly List<string> Uses = new List<string>();
            internal readonly List<string> Notes = new List<string>();
            internal readonly List<TextureImporterPlatformSettings> Changes = new List<TextureImporterPlatformSettings>();
        }

        internal sealed class Result
        {
            internal int Changed;
            internal int Failed;
            internal bool Cancelled;
            internal string BackupRoot;
        }

        internal static bool IsIdle => !EditorApplication.isPlayingOrWillChangePlaymode &&
            !EditorApplication.isCompiling && !EditorApplication.isUpdating && !BuildPipeline.isBuildingPlayer;

        internal static List<Entry> Collect(IEnumerable<Material> materials, int maximum, CrunchMode crunch)
        {
            if (maximum < 32 || maximum > 16384 || !Mathf.IsPowerOfTwo(maximum))
                throw new ArgumentOutOfRangeException(nameof(maximum));

            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (Material material in materials.Where(m => m != null).Distinct())
            {
                if (material.shader == null) continue;
                foreach (string property in material.GetTexturePropertyNames())
                {
                    Texture texture = material.GetTexture(property);
                    if (texture == null) continue;
                    string path = AssetDatabase.GetAssetPath(texture);
                    string key = string.IsNullOrEmpty(path) ? "instance:" + texture.GetInstanceID() : path;
                    if (!entries.TryGetValue(key, out Entry entry))
                    {
                        entry = new Entry { Path = path, Texture = texture };
                        entries.Add(key, entry);
                    }
                    entry.Uses.Add(material.name + " / " + property);
                }
            }

            foreach (Entry entry in entries.Values)
            {
                try { Plan(entry, maximum, crunch); }
                catch (Exception ex)
                {
                    entry.Changes.Clear();
                    entry.Notes.Add("Skipped: " + ex.Message);
                }
                entry.Included = entry.Changes.Count > 0;
            }
            return entries.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        }

        private static void Plan(Entry entry, int maximum, CrunchMode crunch)
        {
            var importer = AssetImporter.GetAtPath(entry.Path) as TextureImporter;
            if (!(entry.Texture is Texture2D) || importer == null ||
                importer.textureShape != TextureImporterShape.Texture2D || importer.textureType == TextureImporterType.Lightmap)
            {
                entry.Notes.Add("Skipped: not an imported 2D material texture (generated textures, lightmaps, cubes and arrays are excluded).");
                return;
            }
            if (!CanEdit(entry.Path, out string reason))
            {
                entry.Notes.Add("Skipped: " + reason);
                return;
            }
            entry.Guid = AssetDatabase.AssetPathToGUID(entry.Path);
            entry.ImporterState = EditorJsonUtility.ToJson(importer);
            importer.GetSourceTextureWidthAndHeight(out int width, out int height);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("Cannot determine source dimensions.");
            entry.Notes.Add($"Source {width} x {height}; imported {entry.Texture.width} x {entry.Texture.height}");

            foreach (TextureImporterPlatformSettings settings in Platforms(importer))
            {
                string label = settings.name == "DefaultTexturePlatform" ? "Default" : settings.name;
                bool changed = false;
                // A small original or an already lower cap needs no size-setting change.
                if (Math.Max(width, height) > maximum && settings.maxTextureSize > maximum)
                {
                    entry.Notes.Add($"{label}: Max Size {settings.maxTextureSize} -> {maximum}");
                    settings.maxTextureSize = maximum;
                    changed = true;
                }
                string extension = System.IO.Path.GetExtension(entry.Path);
                bool hdr = extension.Equals(".exr", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".hdr", StringComparison.OrdinalIgnoreCase) ||
                    UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsHDRFormat(entry.Texture.graphicsFormat);
                changed |= PlanCrunch(settings, crunch, hdr, entry.Notes, label);
                if (changed) entry.Changes.Add(settings);
            }
            if (entry.Changes.Count == 0) entry.Notes.Add("No changes needed.");
        }

        // Unity exposes individual platform settings publicly, but not their complete list.
        // Read the 2022.3 serialized names only; all writes use the public importer API.
        internal static List<TextureImporterPlatformSettings> Platforms(TextureImporter importer)
        {
            var result = new List<TextureImporterPlatformSettings> { importer.GetDefaultPlatformTextureSettings() };
            var names = new HashSet<string>(StringComparer.Ordinal) { result[0].name };
            using (var serialized = new SerializedObject(importer))
            {
                SerializedProperty platforms = serialized.FindProperty("m_PlatformSettings");
                if (platforms == null || !platforms.isArray)
                    throw new InvalidOperationException("Unsupported texture importer platform layout.");
                for (int i = 0; i < platforms.arraySize; i++)
                {
                    SerializedProperty name = platforms.GetArrayElementAtIndex(i).FindPropertyRelative("m_BuildTarget");
                    if (name == null) throw new InvalidOperationException("Unsupported texture importer platform name layout.");
                    if (!names.Add(name.stringValue)) continue;
                    TextureImporterPlatformSettings settings = importer.GetPlatformTextureSettings(name.stringValue);
                    if (settings.overridden) result.Add(settings);
                }
            }
            return result;
        }

        internal static bool PlanCrunch(TextureImporterPlatformSettings settings, CrunchMode mode, bool hdr,
            List<string> notes, string label)
        {
            if (mode == CrunchMode.LeaveUnchanged) return false;
            bool enable = mode == CrunchMode.Enable;
            TextureImporterFormat format = settings.format;
            TextureImporterFormat plain = PlainFormat(format);
            if (enable)
            {
                if (format == TextureImporterFormat.Automatic)
                {
                    if (hdr || settings.textureCompression == TextureImporterCompression.Uncompressed)
                    {
                        notes.Add(label + ": Crunch skipped (HDR or uncompressed Automatic settings).");
                        return false;
                    }
                    // Keep Automatic: Unity chooses a supported format for each target.
                    // Do not force BC7, ASTC, etc. into a different explicit format.
                }
                else
                {
                    TextureImporterFormat packed = CrunchFormat(plain);
                    if (packed == plain)
                    {
                        notes.Add(label + ": Crunch skipped (unsupported format " + format + ").");
                        return false;
                    }
                    settings.format = packed;
                }
            }
            else settings.format = plain;

            bool changed = settings.crunchedCompression != enable || settings.format != format;
            settings.crunchedCompression = enable;
            if (changed) notes.Add(label + ": Crunch " + (enable ? "on" : "off") +
                (settings.format != format ? $" ({format} -> {settings.format})" : "") +
                (enable && format == TextureImporterFormat.Automatic ? " (Automatic: where supported by Unity)" : ""));
            return changed;
        }

        private static TextureImporterFormat PlainFormat(TextureImporterFormat format)
        {
            switch (format)
            {
                case TextureImporterFormat.DXT1Crunched: return TextureImporterFormat.DXT1;
                case TextureImporterFormat.DXT5Crunched: return TextureImporterFormat.DXT5;
                case TextureImporterFormat.ETC_RGB4Crunched: return TextureImporterFormat.ETC_RGB4;
                case TextureImporterFormat.ETC2_RGBA8Crunched: return TextureImporterFormat.ETC2_RGBA8;
                default: return format;
            }
        }

        private static TextureImporterFormat CrunchFormat(TextureImporterFormat format)
        {
            switch (format)
            {
                case TextureImporterFormat.DXT1: return TextureImporterFormat.DXT1Crunched;
                case TextureImporterFormat.DXT5: return TextureImporterFormat.DXT5Crunched;
                case TextureImporterFormat.ETC_RGB4: return TextureImporterFormat.ETC_RGB4Crunched;
                case TextureImporterFormat.ETC2_RGBA8: return TextureImporterFormat.ETC2_RGBA8Crunched;
                default: return format;
            }
        }

        private static bool CanEdit(string path, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(path)) { reason = "No asset path."; return false; }
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                PackageInfo package = PackageInfo.FindForAssetPath(path);
                if (package == null || package.source != PackageSource.Embedded)
                {
                    reason = "Only project assets and embedded package textures can be changed.";
                    return false;
                }
            }
            string meta = path + ".meta";
            if (!File.Exists(meta) || (File.GetAttributes(meta) & FileAttributes.ReadOnly) != 0)
            {
                reason = "Missing or read-only texture metadata.";
                return false;
            }
            return AssetDatabase.IsOpenForEdit(meta, out reason, StatusQueryOptions.ForceUpdate);
        }

        private static TextureImporter Revalidate(Entry entry)
        {
            if (!CanEdit(entry.Path, out string reason)) throw new IOException(entry.Path + ": " + reason);
            var importer = AssetImporter.GetAtPath(entry.Path) as TextureImporter;
            if (importer == null || AssetDatabase.AssetPathToGUID(entry.Path) != entry.Guid ||
                EditorJsonUtility.ToJson(importer) != entry.ImporterState)
                throw new InvalidOperationException(entry.Path + ": Import settings changed since preview. Refresh the preview first.");
            return importer;
        }

        internal static Result Apply(IEnumerable<Entry> entries, Func<int, int, string, bool> cancel = null)
        {
            if (!IsIdle) throw new InvalidOperationException("Wait until Unity is idle and outside Play Mode.");
            List<Entry> selected = entries.Where(e => e.Included && e.Changes.Count > 0)
                .GroupBy(e => e.Path, StringComparer.Ordinal).Select(g => g.First()).ToList();
            var result = new Result();
            if (selected.Count == 0) return result;
            // Refuse a stale preview before making any changes or backups.
            foreach (Entry entry in selected) Revalidate(entry);
            result.BackupRoot = Path.GetFullPath(Path.Combine("Library", "LightbulbWorldTools", "Backups",
                "MaterialTextures", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
            foreach (Entry entry in selected)
            {
                string backup = Path.Combine(result.BackupRoot, entry.Path + ".meta");
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                File.Copy(entry.Path + ".meta", backup, false);
            }
            Debug.Log("[Lightbulb] Original texture metadata backups: " + result.BackupRoot +
                "\nTo restore, close Unity and copy these .meta files to their matching project paths. " +
                "This restores all import settings, not just size/Crunch. Deleting Library removes the backups.");

            for (int i = 0; i < selected.Count; i++)
            {
                Entry entry = selected[i];
                if (cancel != null && cancel(i, selected.Count, entry.Path)) { result.Cancelled = true; break; }
                try
                {
                    TextureImporter importer = Revalidate(entry);
                    foreach (TextureImporterPlatformSettings settings in entry.Changes)
                        importer.SetPlatformTextureSettings(settings);
                    importer.SaveAndReimport();
                    importer = AssetImporter.GetAtPath(entry.Path) as TextureImporter;
                    if (importer == null) throw new InvalidOperationException("Importer missing after reimport.");
                    foreach (TextureImporterPlatformSettings expected in entry.Changes)
                    {
                        TextureImporterPlatformSettings actual = importer.GetPlatformTextureSettings(expected.name);
                        if (actual.maxTextureSize != expected.maxTextureSize || actual.format != expected.format ||
                            actual.crunchedCompression != expected.crunchedCompression || actual.overridden != expected.overridden)
                            throw new InvalidOperationException("Import settings did not stick for " + expected.name + ". Check asset postprocessors.");
                    }
                    result.Changed++;
                    Debug.Log("[Lightbulb] Updated " + entry.Path + "\n" + string.Join("\n", entry.Notes), entry.Texture);
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    Debug.LogError("[Lightbulb] Could not finish " + entry.Path + ": " + ex.Message +
                        "\nOriginal metadata: " + result.BackupRoot, entry.Texture);
                }
            }
            Debug.Log($"[Lightbulb] Material textures: {result.Changed} changed, {result.Failed} failed" +
                (result.Cancelled ? "; cancelled. Completed changes remain applied." : "."));
            return result;
        }
    }
}
