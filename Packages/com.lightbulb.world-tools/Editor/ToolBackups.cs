using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;

namespace Lightbulb.WorldTools
{
    // Shared by all file repairs. The first available original belongs to the asset, not to a run/tool.
    internal static class ToolBackups
    {
        internal const string Root = "Library/LightbulbWorldTools";
        internal const string Originals = Root + "/Originals";
        internal sealed class Copy
        {
            internal string Path, Source, Key, Hash;
            internal DateTime Time;
        }
        internal static string Digest(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        // Unity's Windows Mono file APIs otherwise fail on legacy backup paths longer than 260 characters.
        internal static string FullPath(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path.Substring(8);
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
            return System.IO.Path.GetFullPath(path);
        }
        internal static string IOPath(string path)
        {
            string full = FullPath(path);
            if (System.IO.Path.DirectorySeparatorChar != '\\') return full;
            return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        }
        internal static string SafePath(string root, string path)
        {
            string fullRoot = FullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            string full = FullPath(path);
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path is outside " + root + ": " + path);
            for (string p = full; p != null; p = System.IO.Path.GetDirectoryName(p))
                if ((File.Exists(IOPath(p)) || Directory.Exists(IOPath(p))) && (File.GetAttributes(IOPath(p)) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked paths are not supported: " + p);
            return full;
        }
        static string Key(string source, byte[] bytes)
        {
            if (source.EndsWith(".meta", StringComparison.Ordinal))
            {
                var match = Regex.Match(Encoding.UTF8.GetString(bytes), @"(?m)^guid: ([a-fA-F0-9]{32})\s*$");
                if (match.Success) return "meta-" + match.Groups[1].Value.ToLowerInvariant();
            }
            return "path-" + Digest(Encoding.UTF8.GetBytes(source.ToLowerInvariant()));
        }
        internal static Copy Read(string path, string source, string key = null)
        {
            SafePath(Root, path);
            byte[] bytes = File.ReadAllBytes(IOPath(path));
            string relative = FullPath(path).Substring(FullPath(".").TrimEnd(System.IO.Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
            return new Copy { Path = relative, Source = source, Key = key ?? Key(source, bytes), Hash = Digest(bytes), Time = File.GetLastWriteTimeUtc(IOPath(path)) };
        }
        internal static List<Copy> Inventory()
        {
            var copies = new List<Copy>();
            if (Directory.Exists(IOPath(Originals)))
                foreach (string folder in Directory.GetDirectories(IOPath(SafePath("Library", Originals))).Select(FullPath))
                {
                    string record = folder + "/source.txt";
                    SafePath(Root, record);
                    if (!File.Exists(IOPath(record))) throw new IOException("Incomplete original backup: " + folder);
                    string source = File.ReadAllText(IOPath(record));
                    string path = folder + "/" + System.IO.Path.GetFileName(source);
                    copies.Add(Read(path, source, System.IO.Path.GetFileName(folder)));
                }
            // Temporary legacy inventory: removable after LegacyToolFileMigration has shipped.
            foreach (string category in new[] { "MaterialTextures", "MochieLinearTextures", "VideoPlayerShim" })
            {
                string folder = Root + "/Backups/" + category;
                if (!Directory.Exists(IOPath(folder))) continue;
                foreach (string run in Directory.GetDirectories(IOPath(SafePath("Library", folder))).Select(FullPath))
                    foreach (string path in Files(run))
                    {
                        string source = category == "VideoPlayerShim"
                            ? "Packages/dev.architech.videoplayershim/Editor/PlayModeUrlResolverShim.cs"
                            : path.Substring(run.Length + 1).Replace('\\', '/');
                        if (category == "VideoPlayerShim" && System.IO.Path.GetFileName(path) != "PlayModeUrlResolverShim.cs") continue;
                        if (category != "VideoPlayerShim" && (!source.StartsWith("Assets/") || !source.EndsWith(".meta"))) continue;
                        var copy = Read(path, source);
                        string stamp = System.IO.Path.GetFileName(run);
                        if (stamp.Length >= 15 && DateTime.TryParseExact(stamp.Substring(0, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime time)) copy.Time = time;
                        copies.Add(copy);
                    }
            }
            // Old patch backups did not record their source path. Only associate them when there is one installed target.
            string oldSpecular = Root + "/MochieSpecular";
            if (Directory.Exists(IOPath(oldSpecular)))
            {
                var targets = AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith("Assets/") && p.EndsWith("/StandardLighting.cginc")).ToArray();
                if (targets.Length == 1)
                    foreach (string path in Files(oldSpecular).Where(p => System.IO.Path.GetFileName(p) == "StandardLighting.cginc"))
                    {
                        var copy = Read(path, targets[0]); copy.Time = File.GetCreationTimeUtc(IOPath(path)); copies.Add(copy);
                    }
            }
            return copies;
        }
        static IEnumerable<string> Files(string folder)
        {
            SafePath(Root, folder);
            foreach (string path in Directory.GetFiles(IOPath(folder)).Select(FullPath)) { SafePath(Root, path); yield return path; }
            foreach (string child in Directory.GetDirectories(IOPath(folder)).Select(FullPath))
                foreach (string path in Files(child)) yield return path;
        }
        internal static string Preserve(string source, byte[] bytes = null, List<Copy> inventory = null)
        {
            source = source.Replace('\\', '/');
            SafePath(".", source);
            bytes = bytes ?? File.ReadAllBytes(IOPath(source));
            string key = Key(source, bytes);
            string folder = Originals + "/" + key;
            string existingRecord = folder + "/source.txt";
            SafePath(Root, existingRecord);
            if (File.Exists(IOPath(existingRecord)))
            {
                string existing = folder + "/" + System.IO.Path.GetFileName(File.ReadAllText(IOPath(existingRecord)));
                SafePath(Root, existing);
                if (!File.Exists(IOPath(existing))) throw new IOException("Original backup is missing: " + existing);
                return existing;
            }
            Copy oldest = (inventory ?? Inventory()).Where(c => c.Key == key).OrderBy(c => c.Time).ThenBy(c => c.Path, StringComparer.Ordinal).FirstOrDefault();
            if (oldest != null) return oldest.Path;
            Directory.CreateDirectory(IOPath(folder));
            string target = folder + "/" + System.IO.Path.GetFileName(source);
            // CreateNew prevents a partial or conflicting backup from being overwritten on retry.
            using (var stream = new FileStream(IOPath(target), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            File.WriteAllText(IOPath(existingRecord), source);
            return target;
        }
        internal static void RemoveDuplicate(Copy copy, Copy retained)
        {
            SafePath(Root, copy.Path); SafePath(Root, retained.Path);
            if (copy.Key != retained.Key || copy.Path == retained.Path || copy.Path.StartsWith(Originals + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("Only redundant legacy backups can be removed.");
            if (Digest(File.ReadAllBytes(IOPath(copy.Path))) != copy.Hash || Digest(File.ReadAllBytes(IOPath(retained.Path))) != retained.Hash)
                throw new InvalidOperationException("Backups changed. Scan again.");
            File.Delete(IOPath(copy.Path));
            string folder = System.IO.Path.GetDirectoryName(copy.Path);
            while (folder != null && !string.Equals(FullPath(folder), FullPath(Root), StringComparison.OrdinalIgnoreCase))
            {
                SafePath(Root, folder);
                if (Directory.EnumerateFileSystemEntries(IOPath(folder)).Any()) break;
                Directory.Delete(IOPath(folder)); // Empty directories only, never recursive.
                folder = System.IO.Path.GetDirectoryName(folder);
            }
        }
    }
}
