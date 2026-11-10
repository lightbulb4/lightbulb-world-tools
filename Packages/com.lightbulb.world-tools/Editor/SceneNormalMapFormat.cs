using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Entry = Lightbulb.WorldTools.MaterialTextureBatch.Entry;

namespace Lightbulb.WorldTools
{
    internal static class SceneNormalMapFormat
    {
        internal const string Platform = "Standalone";
        internal const string RestorePath = ToolBackups.Root + "/NormalMapFormats.json";
        internal enum Preset { Quality, DownloadSize }
        internal static readonly string[] PresetLabels =
        {
            "Quality — BC5 (no Crunch)", "Download size — DXT5 + Crunch (100)"
        };
        internal static readonly TextureImporterFormat[] Formats =
        {
            TextureImporterFormat.DXT5Crunched,
            TextureImporterFormat.BC5, TextureImporterFormat.BC7,
            TextureImporterFormat.DXT5, TextureImporterFormat.RGBA32
        };

        [Serializable]
        internal sealed class Record
        {
            public string Guid;
            public string Before;
            public string After;
            public string PendingPrevious;
            public bool HadPlatform;
        }

        [Serializable]
        internal sealed class Journal
        {
            public int Version = 1;
            public List<Record> Records = new List<Record>();
        }

        internal sealed class Result
        {
            internal int Changed, Failed;
            internal bool Cancelled;
            internal readonly List<string> Messages = new List<string>();
            public override string ToString() => $"{Changed} changed; {Failed} failed or skipped." +
                (Cancelled ? " Cancelled; completed changes remain applied." : "") +
                (Messages.Count == 0 ? "" : "\n" + string.Join("\n", Messages));
        }

        internal static Journal Load(string path = RestorePath)
        {
            ToolBackups.SafePath(ToolBackups.Root, path);
            if (!File.Exists(path)) return new Journal();
            var journal = JsonUtility.FromJson<Journal>(File.ReadAllText(path));
            if (journal == null || journal.Version != 1 || journal.Records == null ||
                journal.Records.Any(r => r == null || string.IsNullOrEmpty(r.Guid) ||
                    string.IsNullOrEmpty(r.Before) || string.IsNullOrEmpty(r.After)) ||
                journal.Records.Select(r => r.Guid).Distinct().Count() != journal.Records.Count)
                throw new InvalidOperationException("Invalid normal-map restore record: " + path);
            return journal;
        }

        private static void Save(Journal journal, string path)
        {
            ToolBackups.SafePath(ToolBackups.Root, path);
            ToolBackups.SafePath(ToolBackups.Root, path + ".tmp");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Persist recovery before changing an importer; replace atomically after flushing to disk.
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(journal, true));
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
            else File.Move(path + ".tmp", path);
        }

        private static string Snapshot(TextureImporter importer) => JsonUtility.ToJson(importer.GetPlatformTextureSettings(Platform));

        internal static bool HasPlatform(TextureImporter importer)
        {
            using (var serialized = new SerializedObject(importer))
            {
                var platforms = serialized.FindProperty("m_PlatformSettings");
                if (platforms == null || !platforms.isArray)
                    throw new InvalidOperationException("Unsupported texture importer platform layout.");
                for (int i = 0; i < platforms.arraySize; i++)
                    if (platforms.GetArrayElementAtIndex(i).FindPropertyRelative("m_BuildTarget").stringValue == Platform)
                        return true;
                return false;
            }
        }

        internal static List<Entry> CollectPreset(IEnumerable<Material> materials, Preset preset)
        {
            switch (preset)
            {
                case Preset.Quality: return Collect(materials, TextureImporterFormat.BC5);
                case Preset.DownloadSize: return Collect(materials, TextureImporterFormat.DXT5Crunched, 100, false);
                default: throw new ArgumentOutOfRangeException(nameof(preset));
            }
        }

        internal static List<Entry> Collect(IEnumerable<Material> materials, TextureImporterFormat format,
            int crunchQuality = 50, bool preserveExistingCrunchQuality = true)
        {
            if (crunchQuality < 0 || crunchQuality > 100) throw new ArgumentOutOfRangeException(nameof(crunchQuality));
            if (!Formats.Contains(format) || !TextureImporter.IsPlatformTextureFormatValid(
                TextureImporterType.NormalMap, BuildTarget.StandaloneWindows64, format))
                throw new ArgumentException("Unsupported desktop normal-map format.", nameof(format));
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (Material material in materials.Where(m => m != null && m.shader != null).Distinct())
                foreach (string property in material.GetTexturePropertyNames())
                {
                    Texture texture = material.GetTexture(property);
                    string path = AssetDatabase.GetAssetPath(texture);
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null || importer.textureType != TextureImporterType.NormalMap) continue;
                    if (!entries.TryGetValue(path, out Entry entry))
                    {
                        entry = new Entry { Path = path, Texture = texture };
                        entries.Add(path, entry);
                    }
                    entry.Uses.Add(material.name + " / " + property);
                }

            foreach (Entry entry in entries.Values)
            {
                try
                {
                    var importer = (TextureImporter)AssetImporter.GetAtPath(entry.Path);
                    if (!(entry.Texture is Texture2D) || importer.textureShape != TextureImporterShape.Texture2D)
                        throw new InvalidOperationException("Only imported 2D normal maps are supported.");
                    if (!MaterialTextureBatch.CanEdit(entry.Path, out string reason)) throw new IOException(reason);
                    entry.Guid = AssetDatabase.AssetPathToGUID(entry.Path);
                    entry.ImporterState = EditorJsonUtility.ToJson(importer);
                    var before = importer.GetPlatformTextureSettings(Platform);
                    var effective = before.overridden ? before : importer.GetDefaultPlatformTextureSettings();
                    var previousFormat = effective.format == TextureImporterFormat.Automatic ? importer.GetAutomaticFormat(Platform) : effective.format;
                    if (!TextureImporter.IsPlatformTextureFormatValid(TextureImporterType.NormalMap, BuildTarget.StandaloneWindows64, previousFormat))
                    {
                        var fallback = importer.GetAutomaticFormat(Platform);
                        entry.Notes.Add($"Stored {previousFormat} is unsupported for normal maps; Unity falls back to {fallback}.");
                        previousFormat = fallback;
                    }
                    bool crunch = format == TextureImporterFormat.DXT5Crunched;
                    bool wasCrunched = previousFormat == TextureImporterFormat.DXT5Crunched || previousFormat == TextureImporterFormat.DXT1Crunched;
                    if (crunch && (previousFormat == TextureImporterFormat.DXT1 || previousFormat == TextureImporterFormat.DXT1Crunched ||
                        previousFormat == TextureImporterFormat.BC4))
                        throw new InvalidOperationException("Already uses a 4-bit GPU format. DXT5 would double its GPU memory; left unchanged.");
                    int quality = preserveExistingCrunchQuality && wasCrunched ? effective.compressionQuality : crunchQuality;
                    entry.Notes.Add($"Imported {entry.Texture.width} x {entry.Texture.height}; desktop Max Size {effective.maxTextureSize} (preserved)");
                    // An inherited Crunch format is already optimized: don't create an unnecessary override.
                    if (crunch && previousFormat == format && effective.compressionQuality == quality)
                    {
                        entry.Notes.Add($"Already uses DXT5 + Crunch · Quality: {quality} (preserved)");
                        entry.Included = false;
                        continue;
                    }
                    // Disabled overrides may contain stale size caps. Inherit effective defaults when enabling.
                    var after = before.overridden ? importer.GetPlatformTextureSettings(Platform) : importer.GetDefaultPlatformTextureSettings();
                    after.name = Platform;
                    after.overridden = true;
                    if (crunch)
                    {
                        after.format = TextureImporterFormat.DXT5;
                        after.crunchedCompression = wasCrunched;
                        MaterialTextureBatch.PlanCrunch(after, MaterialTextureBatch.CrunchMode.Enable, false, entry.Notes, "Desktop", quality);
                    }
                    else
                    {
                        MaterialTextureBatch.PlanCrunch(after, MaterialTextureBatch.CrunchMode.Disable, false, entry.Notes, "Desktop");
                        after.format = format;
                    }
                    if (JsonUtility.ToJson(before) != JsonUtility.ToJson(after))
                    {
                        entry.Changes.Add(after);
                        entry.Notes.Add($"Desktop: {previousFormat} → {format}" + (before.overridden ? "" : " · Enable override"));
                        if (format != TextureImporterFormat.RGBA32)
                        {
                            if (previousFormat == TextureImporterFormat.RGBA32)
                                entry.Notes.Add("GPU texture data: 75% smaller at the same resolution.");
                            else if (previousFormat == TextureImporterFormat.BC5 || previousFormat == TextureImporterFormat.BC7 ||
                                previousFormat == TextureImporterFormat.DXT5 || previousFormat == TextureImporterFormat.DXT5Crunched)
                                entry.Notes.Add("GPU texture data: unchanged (8 bits per pixel).");
                        }
                        if (!crunch && wasCrunched) entry.Notes.Add("Download warning: removing Crunch can increase build size.");
                    }
                    else entry.Notes.Add("Already matches.");
                }
                catch (Exception ex) { entry.Changes.Clear(); entry.Notes.Add("Skipped: " + ex.Message); }
                entry.Included = entry.Changes.Count > 0;
            }
            return entries.Values.OrderByDescending(e => Math.Max(e.Texture.width, e.Texture.height))
                .ThenBy(e => e.Path, StringComparer.Ordinal).ToList();
        }

        private static void RequireIdle()
        {
            if (!SceneMaterials.IsLightingIdle) throw new InvalidOperationException("Wait until Unity is idle and outside Play Mode or baking.");
        }

        internal static Result Apply(IEnumerable<Entry> entries, Func<int, int, string, bool> cancel = null, string journalPath = RestorePath)
        {
            RequireIdle();
            var selected = entries.Where(e => e.Included && e.Changes.Count > 0).GroupBy(e => e.Guid).Select(g => g.First()).ToList();
            var journal = Load(journalPath);
            // Reject the complete stale batch before creating any backups or changes.
            foreach (Entry entry in selected)
            {
                var importer = MaterialTextureBatch.Revalidate(entry);
                var record = journal.Records.FirstOrDefault(r => r.Guid == entry.Guid);
                if (record != null && Snapshot(importer) != record.After)
                    throw new InvalidOperationException(entry.Path + ": Desktop settings changed after an earlier conversion. Restore or resolve that change first.");
            }
            var result = new Result();
            var originals = ToolBackups.Inventory();
            for (int i = 0; i < selected.Count; i++)
            {
                Entry entry = selected[i];
                if (cancel != null && cancel(i, selected.Count, entry.Path)) { result.Cancelled = true; break; }
                try
                {
                    var importer = MaterialTextureBatch.Revalidate(entry);
                    ToolBackups.Preserve(entry.Path + ".meta", inventory: originals);
                    var record = journal.Records.FirstOrDefault(r => r.Guid == entry.Guid);
                    if (record == null)
                    {
                        record = new Record { Guid = entry.Guid, Before = Snapshot(importer), HadPlatform = HasPlatform(importer) };
                        journal.Records.Add(record);
                    }
                    record.PendingPrevious = Snapshot(importer);
                    record.After = JsonUtility.ToJson(entry.Changes.Single());
                    Save(journal, journalPath);
                    importer.SetPlatformTextureSettings(entry.Changes.Single());
                    importer.SaveAndReimport();
                    importer = (TextureImporter)AssetImporter.GetAtPath(entry.Path);
                    if (Snapshot(importer) != record.After)
                        throw new InvalidOperationException("Desktop settings did not stick. Check asset postprocessors; the restore record is retained.");
                    record.PendingPrevious = null;
                    Save(journal, journalPath);
                    result.Changed++;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Messages.Add(entry.Path + ": " + ex.Message);
                    // A persistence/import failure must not lead to further writes with uncertain recovery state.
                    break;
                }
            }
            return result;
        }

        internal static Result Restore(Func<int, int, string, bool> cancel = null, string journalPath = RestorePath)
        {
            RequireIdle();
            var journal = Load(journalPath);
            var records = journal.Records.ToArray();
            var result = new Result();
            for (int i = 0; i < records.Length; i++)
            {
                var record = records[i];
                string path = AssetDatabase.GUIDToAssetPath(record.Guid);
                if (cancel != null && cancel(i, records.Length, path)) { result.Cancelled = true; break; }
                try
                {
                    if (!MaterialTextureBatch.CanEdit(path, out string reason)) throw new IOException(reason);
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null || importer.textureType != TextureImporterType.NormalMap)
                        throw new InvalidOperationException("No longer an imported normal map.");
                    bool alreadyRestored = HasPlatform(importer) == record.HadPlatform &&
                        (!record.HadPlatform || Snapshot(importer) == record.Before);
                    if (!alreadyRestored)
                    {
                        if (Snapshot(importer) != record.After && Snapshot(importer) != record.PendingPrevious)
                            throw new InvalidOperationException("Desktop settings changed after conversion. Left untouched; restore record retained.");
                        if (record.HadPlatform)
                            importer.SetPlatformTextureSettings(JsonUtility.FromJson<TextureImporterPlatformSettings>(record.Before));
                        else importer.ClearPlatformTextureSettings(Platform);
                        importer.SaveAndReimport();
                        importer = (TextureImporter)AssetImporter.GetAtPath(path);
                        if (HasPlatform(importer) != record.HadPlatform || (record.HadPlatform && Snapshot(importer) != record.Before))
                            throw new InvalidOperationException("Restore did not stick. Check asset postprocessors; restore record retained.");
                    }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Messages.Add((string.IsNullOrEmpty(path) ? record.Guid : path) + ": " + ex.Message);
                    continue;
                }
                journal.Records.Remove(record);
                Save(journal, journalPath);
                result.Changed++;
            }
            return result;
        }
    }
}
