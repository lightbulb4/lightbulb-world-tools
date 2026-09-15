using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools.Tests
{
    public class ToolBackupsTests
    {
        string root, id;
        readonly List<string> backupFiles = new List<string>();
        [SetUp] public void Setup()
        {
            id = Guid.NewGuid().ToString("N");
            root = "Assets/__LightbulbCleanupTests_" + id;
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(root));
        }
        [TearDown] public void TearDown()
        {
            foreach (string file in backupFiles)
            {
                ToolBackups.SafePath(ToolBackups.Root, file);
                if (File.Exists(file)) File.Delete(file);
                string folder = Path.GetDirectoryName(file);
                while (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    ToolBackups.SafePath(ToolBackups.Root, folder); Directory.Delete(folder); folder = Path.GetDirectoryName(folder);
                }
            }
            backupFiles.Clear();
            Assert.That(root, Does.StartWith("Assets/__LightbulbCleanupTests_"));
            AssetDatabase.DeleteAsset(root);
        }
        string Meta(string name) => root + "/" + name + ".png.meta";
        byte[] Data(string value) => System.Text.Encoding.UTF8.GetBytes("fileFormatVersion: 2\nguid: " + id + "\nvalue: " + value + "\n");
        void Track(string path)
        {
            backupFiles.Add(path);
            string record = Path.GetDirectoryName(path) + "/source.txt";
            if (File.Exists(record)) backupFiles.Add(record);
        }
        string Legacy(string category, string stamp, byte[] data)
        {
            string path = ToolBackups.Root + "/Backups/" + category + "/" + stamp + "-" + id + "/" + Meta("map");
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, data); Track(path); return path;
        }
        [Test] public void RepeatedChangesAndAssetRenameKeepOneOriginal()
        {
            string first = ToolBackups.Preserve(Meta("original"), Data("first")); Track(first);
            Assert.That(ToolBackups.Preserve(Meta("original"), Data("second")), Is.EqualTo(first));
            Assert.That(ToolBackups.Preserve(Meta("renamed"), Data("third")), Is.EqualTo(first));
            Assert.That(File.ReadAllBytes(first), Is.EqualTo(Data("first")));
            Assert.That(ToolBackups.Inventory().Count(c => c.Key == "meta-" + id), Is.EqualTo(1));
        }
        [Test] public void LegacyBackupsAcrossToolsUseEarliestRunNotMetadataModificationTime()
        {
            string early = Legacy("MochieLinearTextures", "20260101-120000", Data("original"));
            string later = Legacy("MaterialTextures", "20260201-120000", Data("later"));
            File.SetLastWriteTimeUtc(later, new DateTime(2000, 1, 1));
            Assert.That(ToolBackups.Preserve(Meta("map"), Data("current")), Is.EqualTo(early));
            Assert.That(ToolBackups.Inventory().Count(c => c.Key == "meta-" + id), Is.EqualTo(2), "No new copy during migration");
            var copies = ToolBackups.Inventory().Where(c => c.Key == "meta-" + id).OrderBy(c => c.Time).ToList();
            ToolBackups.RemoveDuplicate(copies[1], copies[0]);
            Assert.That(File.Exists(later), Is.False); Assert.That(File.ReadAllBytes(early), Is.EqualTo(Data("original")));
        }
        [Test] public void ChangedRetainedBackupPreventsDeletingHistory()
        {
            string first = Legacy("MaterialTextures", "20260101-120000", Data("original"));
            string second = Legacy("MaterialTextures", "20260201-120000", Data("later"));
            var copies = ToolBackups.Inventory().Where(c => c.Key == "meta-" + id).OrderBy(c => c.Time).ToList();
            File.WriteAllBytes(first, Data("edited"));
            Assert.Throws<InvalidOperationException>(() => ToolBackups.RemoveDuplicate(copies[1], copies[0]));
            Assert.That(File.Exists(second), Is.True);
        }
        [Test] public void OutsidePathsAreRejected()
        {
            Assert.Throws<IOException>(() => ToolBackups.SafePath(ToolBackups.Root, "Assets/outside"));
            Assert.Throws<IOException>(() => ToolBackups.Preserve("../outside.cs", new byte[] { 1 }));
        }
        [Test] public void ShaderPatchCyclesKeepUnpatchedOriginal()
        {
            string source = root + "/shader.cginc";
            string first = ToolBackups.Preserve(source, new byte[] { 1, 2 }); Track(first);
            Assert.That(ToolBackups.Preserve(source, new byte[] { 3, 4 }), Is.EqualTo(first));
            Assert.That(File.ReadAllBytes(first), Is.EqualTo(new byte[] { 1, 2 }));
        }
        [Test] public void UVHelpersAreTemporaryAndReleased()
        {
            string[] before = AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith("Assets/Resources/Lines_Colored_Blended")).ToArray();
            var line = B83.UVViewer.Drawing.LineMat;
            var depth = B83.UVViewer.Drawing.LineMatDepthTest;
            try
            {
                Assert.That(line.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
                Assert.That(depth.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
                Assert.That(AssetDatabase.GetAssetPath(line), Is.Empty);
                Assert.That(line.HasProperty("_ZTest"), Is.True);
                Assert.That(line.GetInt("_ZTest"), Is.EqualTo((int)UnityEngine.Rendering.CompareFunction.Always));
                Assert.That(depth.GetInt("_ZTest"), Is.EqualTo((int)UnityEngine.Rendering.CompareFunction.LessEqual));
                Assert.That(AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith("Assets/Resources/Lines_Colored_Blended")), Is.EquivalentTo(before));
            }
            finally { B83.UVViewer.Drawing.ReleaseMaterials(); }
            Assert.That(line == null && depth == null, Is.True);
        }
    }
}
