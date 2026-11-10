using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools.Tests
{
    public class SceneNormalMapFormatTests
    {
        private string root, journalPath;
        private readonly List<Object> temporary = new List<Object>();

        [UnitySetUp]
        public IEnumerator WaitForIdle()
        {
            yield return null;
            for (int i = 0; i < 300 && !SceneMaterials.IsLightingIdle; i++) yield return null;
            Assert.That(SceneMaterials.IsLightingIdle, Is.True, "Editor must finish startup/imports before testing mutations.");
        }

        [SetUp]
        public void SetUp()
        {
            string id = Guid.NewGuid().ToString("N");
            root = "Assets/__NormalMapFormatTests_" + id;
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(root));
            journalPath = ToolBackups.Root + "/NormalMapTests-" + id + ".json";
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object value in temporary) if (value != null) Object.DestroyImmediate(value);
            temporary.Clear();
            Assert.That(root, Does.StartWith("Assets/__NormalMapFormatTests_"));
            AssetDatabase.DeleteAsset(root);
            if (File.Exists(journalPath)) File.Delete(journalPath);
            if (File.Exists(journalPath + ".tmp")) File.Delete(journalPath + ".tmp");
        }

        private Texture2D Texture(string name, bool normal = true)
        {
            var image = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            image.SetPixels(Enumerable.Repeat(new Color(0.5f, 0.5f, 1f, 1f), 1024).ToArray());
            image.Apply();
            string path = root + "/" + name + ".png";
            File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.maxTextureSize = 1024;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private Material Material(Texture texture)
        {
            var material = new Material(Shader.Find("Standard"));
            temporary.Add(material);
            material.SetTexture("_BumpMap", texture);
            return material;
        }

        private static TextureImporter Importer(Texture texture) => (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));
        private static string Desktop(Texture texture) => JsonUtility.ToJson(Importer(texture).GetPlatformTextureSettings("Standalone"));
        private List<MaterialTextureBatch.Entry> Collect(Material material, TextureImporterFormat format = TextureImporterFormat.BC5) =>
            SceneNormalMapFormat.Collect(new[] { material }, format);
        private SceneNormalMapFormat.Result Apply(Material material, TextureImporterFormat format = TextureImporterFormat.BC5) =>
            SceneNormalMapFormat.Apply(Collect(material, format), journalPath: journalPath);

        [Test]
        public void PreviewDeduplicatesAndOnlyIncludesTexturesImportedAsNormalMaps()
        {
            var texture = Texture("normal");
            var material = Material(texture);
            material.SetTexture("_DetailNormalMap", texture);
            var color = Material(Texture("not_a_normal", false));
            byte[] original = File.ReadAllBytes(AssetDatabase.GetAssetPath(texture) + ".meta");
            var entries = SceneNormalMapFormat.Collect(new[] { material, material, color }, TextureImporterFormat.BC5);
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].Uses.Count, Is.EqualTo(2));
            Assert.That(entries[0].Changes.Single().format, Is.EqualTo(TextureImporterFormat.BC5));
            Assert.That(File.ReadAllBytes(entries[0].Path + ".meta"), Is.EqualTo(original));
            Assert.That(File.Exists(journalPath), Is.False);
        }

        [TestCase(TextureImporterFormat.BC5)]
        [TestCase(TextureImporterFormat.BC7)]
        [TestCase(TextureImporterFormat.DXT5)]
        [TestCase(TextureImporterFormat.RGBA32)]
        public void ConversionAndRestorePreserveResolutionOtherPlatformsAndSource(TextureImporterFormat format)
        {
            var texture = Texture("normal");
            var importer = Importer(texture);
            var desktop = importer.GetPlatformTextureSettings("Standalone");
            desktop.overridden = true;
            desktop.format = TextureImporterFormat.DXT5Crunched;
            desktop.crunchedCompression = true;
            desktop.maxTextureSize = 512;
            desktop.compressionQuality = 73;
            importer.SetPlatformTextureSettings(desktop);
            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 256;
            importer.SetPlatformTextureSettings(android);
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
            string before = Desktop(texture);
            string defaults = JsonUtility.ToJson(importer.GetDefaultPlatformTextureSettings());
            string mobile = JsonUtility.ToJson(importer.GetPlatformTextureSettings("Android"));
            byte[] source = File.ReadAllBytes(importer.assetPath);
            var material = Material(texture);

            var result = Apply(material, format);
            Assert.That(result.Failed, Is.Zero, result.ToString());
            Assert.That(result.Changed, Is.EqualTo(1));
            importer = Importer(texture);
            Assert.That(importer.GetPlatformTextureSettings("Standalone").format, Is.EqualTo(format));
            Assert.That(importer.GetPlatformTextureSettings("Standalone").maxTextureSize, Is.EqualTo(512));
            Assert.That(importer.GetPlatformTextureSettings("Standalone").crunchedCompression, Is.False);
            Assert.That(JsonUtility.ToJson(importer.GetDefaultPlatformTextureSettings()), Is.EqualTo(defaults));
            Assert.That(JsonUtility.ToJson(importer.GetPlatformTextureSettings("Android")), Is.EqualTo(mobile));
            Assert.That(importer.mipmapEnabled, Is.False);
            Assert.That(File.ReadAllBytes(importer.assetPath), Is.EqualTo(source));
            Assert.That(texture.width, Is.EqualTo(32));
            Assert.That(Collect(material, format).Single().Changes, Is.Empty);
            Assert.That(SceneNormalMapFormat.Load(journalPath).Records.Count, Is.EqualTo(1));

            result = SceneNormalMapFormat.Restore(journalPath: journalPath);
            Assert.That(result.Failed, Is.Zero, result.ToString());
            Assert.That(Desktop(texture), Is.EqualTo(before));
            Assert.That(SceneNormalMapFormat.Load(journalPath).Records, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NewOverridesInheritEffectiveSizeAndRestoreAbsentOrDisabledState(bool disabledOverride)
        {
            var texture = Texture("normal");
            var importer = Importer(texture);
            importer.ClearPlatformTextureSettings("Standalone");
            if (disabledOverride)
            {
                var settings = importer.GetPlatformTextureSettings("Standalone");
                settings.overridden = false;
                settings.maxTextureSize = 256;
                importer.SetPlatformTextureSettings(settings);
            }
            importer.SaveAndReimport();
            bool hadPlatform = SceneNormalMapFormat.HasPlatform(importer);
            string before = Desktop(texture);
            var material = Material(texture);
            Assert.That(Apply(material).Failed, Is.Zero);
            Assert.That(Importer(texture).GetPlatformTextureSettings("Standalone").maxTextureSize, Is.EqualTo(1024));
            Assert.That(Apply(material, TextureImporterFormat.BC7).Failed, Is.Zero);
            Assert.That(SceneNormalMapFormat.Load(journalPath).Records.Single().Before, Is.EqualTo(before));
            var result = SceneNormalMapFormat.Restore(journalPath: journalPath);
            Assert.That(result.Failed, Is.Zero, result.ToString());
            Assert.That(SceneNormalMapFormat.HasPlatform(Importer(texture)), Is.EqualTo(hadPlatform));
            Assert.That(Desktop(texture), Is.EqualTo(before));
        }

        [Test]
        public void RestoreFollowsGuidAfterRenameAndPreservesUnrelatedImporterEdits()
        {
            var texture = Texture("normal");
            var material = Material(texture);
            Assert.That(Apply(material).Failed, Is.Zero);
            Assert.That(AssetDatabase.MoveAsset(AssetDatabase.GetAssetPath(texture), root + "/renamed.png"), Is.Empty);
            var importer = Importer(texture);
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
            var result = SceneNormalMapFormat.Restore(journalPath: journalPath);
            Assert.That(result.Failed, Is.Zero, result.ToString());
            Assert.That(Importer(texture).wrapMode, Is.EqualTo(TextureWrapMode.Clamp));
            Assert.That(Importer(texture).mipmapEnabled, Is.False);
        }

        [Test]
        public void RestoreSkipsLaterDesktopEditsAndRetainsRecovery()
        {
            var texture = Texture("normal");
            Assert.That(Apply(Material(texture)).Failed, Is.Zero);
            var importer = Importer(texture);
            var desktop = importer.GetPlatformTextureSettings("Standalone");
            desktop.maxTextureSize = 256;
            importer.SetPlatformTextureSettings(desktop);
            importer.SaveAndReimport();
            string changed = Desktop(texture);
            var result = SceneNormalMapFormat.Restore(journalPath: journalPath);
            Assert.That(result.Failed, Is.EqualTo(1));
            Assert.That(result.Changed, Is.Zero);
            Assert.That(Desktop(texture), Is.EqualTo(changed));
            Assert.That(SceneNormalMapFormat.Load(journalPath).Records.Count, Is.EqualTo(1));
        }

        [Test]
        public void StaleBatchIsRejectedBeforeAnyTextureChanges()
        {
            var first = Texture("first");
            var second = Texture("second");
            var entries = SceneNormalMapFormat.Collect(new[] { Material(first), Material(second) }, TextureImporterFormat.BC5);
            string before = Desktop(first);
            var importer = Importer(second);
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
            Assert.Throws<InvalidOperationException>(() => SceneNormalMapFormat.Apply(entries, journalPath: journalPath));
            Assert.That(Desktop(first), Is.EqualTo(before));
            Assert.That(File.Exists(journalPath), Is.False);
        }

        [Test]
        public void CancellationOnlyRecordsCompletedTexturesAndRestoreCanResume()
        {
            var entries = SceneNormalMapFormat.Collect(new[] { Material(Texture("first")), Material(Texture("second")) }, TextureImporterFormat.BC5);
            var result = SceneNormalMapFormat.Apply(entries, (i, count, path) => i == 1, journalPath);
            Assert.That(result.Changed, Is.EqualTo(1));
            Assert.That(result.Cancelled, Is.True);
            Assert.That(SceneNormalMapFormat.Load(journalPath).Records.Count, Is.EqualTo(1));
            result = SceneNormalMapFormat.Restore((i, count, path) => true, journalPath);
            Assert.That(result.Cancelled, Is.True);
            Assert.That(SceneNormalMapFormat.Load(journalPath).Records.Count, Is.EqualTo(1));
            Assert.That(SceneNormalMapFormat.Restore(journalPath: journalPath).Changed, Is.EqualTo(1));
        }

        [Test]
        public void SceneScanIncludesInactiveRenderersAndMaterialAnimationSwaps()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var visible = Material(Texture("visible"));
                var swapped = Material(Texture("swapped"));
                var outside = Material(Texture("not_referenced"));
                var go = new GameObject("inactive");
                SceneManager.MoveGameObjectToScene(go, scene);
                go.AddComponent<MeshRenderer>().sharedMaterial = visible;
                var clip = new AnimationClip { legacy = true };
                temporary.Add(clip);
                AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(MeshRenderer), "m_Materials.Array.data[0]"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = swapped } });
                go.AddComponent<Animation>().clip = clip;
                go.SetActive(false);
                var found = SceneReferenceScan.Collect(new[] { scene });
                var entries = SceneNormalMapFormat.Collect(found.Objects.OfType<Material>(), TextureImporterFormat.BC5);
                Assert.That(entries.Select(e => e.Texture), Is.EquivalentTo(new[] { visible.GetTexture("_BumpMap"), swapped.GetTexture("_BumpMap") }));
                Assert.That(entries.Any(e => e.Texture == outside.GetTexture("_BumpMap")), Is.False);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void RestoreRecoversInterruptedRepeatedConversionBeforeImporterWasChanged()
        {
            var texture = Texture("normal");
            var material = Material(texture);
            string before = Desktop(texture);
            Assert.That(Apply(material).Failed, Is.Zero);
            var journal = SceneNormalMapFormat.Load(journalPath);
            var record = journal.Records.Single();
            record.PendingPrevious = record.After;
            record.After = JsonUtility.ToJson(Collect(material, TextureImporterFormat.BC7).Single().Changes.Single());
            File.WriteAllText(journalPath, JsonUtility.ToJson(journal));
            Assert.That(SceneNormalMapFormat.Restore(journalPath: journalPath).Failed, Is.Zero);
            Assert.That(Desktop(texture), Is.EqualTo(before));
        }

        [TestCase(TextureImporterFormat.BC5)]
        [TestCase(TextureImporterFormat.BC7)]
        [TestCase(TextureImporterFormat.DXT5)]
        [TestCase(TextureImporterFormat.RGBA32)]
        public void SizePresetConvertsCompressedAndUncompressedNormalsAndRestoresTheirSettings(TextureImporterFormat sourceFormat)
        {
            var texture = Texture("normal");
            var importer = Importer(texture);
            var settings = importer.GetPlatformTextureSettings("Standalone");
            settings.overridden = true;
            settings.format = sourceFormat;
            settings.maxTextureSize = 512;
            settings.compressionQuality = 100;
            importer.SetPlatformTextureSettings(settings);
            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 256;
            importer.SetPlatformTextureSettings(android);
            importer.SaveAndReimport();
            string before = Desktop(texture);
            string defaults = JsonUtility.ToJson(importer.GetDefaultPlatformTextureSettings());
            string mobile = JsonUtility.ToJson(importer.GetPlatformTextureSettings("Android"));
            var material = Material(texture);
            var entries = SceneNormalMapFormat.Collect(new[] { material }, TextureImporterFormat.DXT5Crunched, 73);
            Assert.That(entries.Single().Notes, Has.Some.Contains(sourceFormat == TextureImporterFormat.RGBA32 ? "75% smaller" : "unchanged (8 bits"));
            var result = SceneNormalMapFormat.Apply(entries, journalPath: journalPath);
            Assert.That(result.Failed, Is.Zero, result.ToString());
            Assert.That(result.Changed, Is.EqualTo(1));
            importer = Importer(texture);
            settings = importer.GetPlatformTextureSettings("Standalone");
            Assert.That(settings.format, Is.EqualTo(TextureImporterFormat.DXT5Crunched));
            Assert.That(settings.crunchedCompression, Is.True);
            Assert.That(settings.compressionQuality, Is.EqualTo(73));
            Assert.That(settings.maxTextureSize, Is.EqualTo(512));
            Assert.That(texture.format, Is.EqualTo(TextureFormat.DXT5Crunched));
            Assert.That(JsonUtility.ToJson(importer.GetDefaultPlatformTextureSettings()), Is.EqualTo(defaults));
            Assert.That(JsonUtility.ToJson(importer.GetPlatformTextureSettings("Android")), Is.EqualTo(mobile));
            Assert.That(SceneNormalMapFormat.Collect(new[] { material }, TextureImporterFormat.DXT5Crunched).Single().Changes, Is.Empty);
            Assert.That(SceneNormalMapFormat.Restore(journalPath: journalPath).Failed, Is.Zero);
            Assert.That(Desktop(texture), Is.EqualTo(before));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SizePresetKeepsExistingCrunchQualityIncludingInheritedSettings(bool overridden)
        {
            var texture = Texture("normal");
            var importer = Importer(texture);
            var settings = overridden ? importer.GetPlatformTextureSettings("Standalone") : importer.GetDefaultPlatformTextureSettings();
            settings.overridden = overridden;
            settings.format = TextureImporterFormat.Automatic;
            settings.crunchedCompression = true;
            settings.compressionQuality = 100;
            importer.SetPlatformTextureSettings(settings);
            importer.SaveAndReimport();
            var material = Material(texture);
            Assert.That(texture.format, Is.EqualTo(TextureFormat.DXT5Crunched));
            byte[] metadata = File.ReadAllBytes(importer.assetPath + ".meta");
            var entries = SceneNormalMapFormat.Collect(new[] { material }, TextureImporterFormat.DXT5Crunched, 50);
            Assert.That(entries.Single().Changes, Is.Empty);
            Assert.That(File.ReadAllBytes(importer.assetPath + ".meta"), Is.EqualTo(metadata));
            entries = SceneNormalMapFormat.Collect(new[] { material }, TextureImporterFormat.DXT5Crunched, 50, false);
            Assert.That(entries.Single().Changes.Single().compressionQuality, Is.EqualTo(50));
            var result = SceneNormalMapFormat.Apply(entries, journalPath: journalPath);
            Assert.That(result.Failed, Is.Zero, result.ToString());
            Assert.That(Importer(texture).GetPlatformTextureSettings("Standalone").compressionQuality, Is.EqualTo(50));
            Assert.That(SceneNormalMapFormat.Restore(journalPath: journalPath).Failed, Is.Zero);
        }

        [Test]
        public void SizePresetCompressesUncompressedDefaultsWithoutChangingThem()
        {
            var texture = Texture("normal");
            var importer = Importer(texture);
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            string before = Desktop(texture);
            Assert.That(Apply(Material(texture), TextureImporterFormat.DXT5Crunched).Failed, Is.Zero);
            Assert.That(texture.format, Is.EqualTo(TextureFormat.DXT5Crunched));
            Assert.That(Importer(texture).textureCompression, Is.EqualTo(TextureImporterCompression.Uncompressed));
            Assert.That(SceneNormalMapFormat.Restore(journalPath: journalPath).Failed, Is.Zero);
            Assert.That(Desktop(texture), Is.EqualTo(before));
        }

        [Test]
        public void SizePresetAfterEarlierBC5ConversionRetainsTheFirstRestorePoint()
        {
            var texture = Texture("normal");
            var material = Material(texture);
            string before = Desktop(texture);
            Assert.That(Apply(material).Failed, Is.Zero);
            Assert.That(Apply(material, TextureImporterFormat.DXT5Crunched).Failed, Is.Zero);
            Assert.That(SceneNormalMapFormat.Load(journalPath).Records.Single().Before, Is.EqualTo(before));
            Assert.That(SceneNormalMapFormat.Restore(journalPath: journalPath).Failed, Is.Zero);
            Assert.That(Desktop(texture), Is.EqualTo(before));
        }

        [Test]
        public void SizePresetUsesTheActualFallbackForUnsupportedStoredFourBitFormats()
        {
            var texture = Texture("normal");
            var importer = Importer(texture);
            var settings = importer.GetPlatformTextureSettings("Standalone");
            settings.overridden = true;
            settings.format = TextureImporterFormat.DXT1;
            const string warning = "Selected texture format 'RGB Compressed DXT1|BC1' for platform 'Standalone' is not valid with the current texture type 'NormalMap'.";
            LogAssert.Expect(LogType.Error, warning);
            importer.SetPlatformTextureSettings(settings);
            importer.SaveAndReimport();
            Assert.That(texture.format, Is.EqualTo(TextureFormat.DXT5));
            string before = Desktop(texture);
            var entry = Collect(Material(texture), TextureImporterFormat.DXT5Crunched).Single();
            Assert.That(entry.Changes.Single().format, Is.EqualTo(TextureImporterFormat.DXT5Crunched));
            Assert.That(entry.Notes, Has.Some.Contains("Unity falls back to DXT5"));
            Assert.That(entry.Notes, Has.Some.Contains("unchanged (8 bits"));
            Assert.That(SceneNormalMapFormat.Apply(new[] { entry }, journalPath: journalPath).Failed, Is.Zero);
            Assert.That(texture.format, Is.EqualTo(TextureFormat.DXT5Crunched));
            LogAssert.Expect(LogType.Error, warning);
            Assert.That(SceneNormalMapFormat.Restore(journalPath: journalPath).Failed, Is.Zero);
            Assert.That(Desktop(texture), Is.EqualTo(before));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TwoPresetsForceCrunch100OrBC5AndRestoreOriginalQuality(bool overridden)
        {
            var texture = Texture("normal");
            var importer = Importer(texture);
            var settings = overridden ? importer.GetPlatformTextureSettings("Standalone") : importer.GetDefaultPlatformTextureSettings();
            settings.overridden = overridden;
            settings.format = TextureImporterFormat.Automatic;
            settings.crunchedCompression = true;
            settings.compressionQuality = 50;
            importer.SetPlatformTextureSettings(settings);
            importer.SaveAndReimport();
            string original = Desktop(texture);
            var material = Material(texture);
            var entries = SceneNormalMapFormat.CollectPreset(new[] { material }, SceneNormalMapFormat.Preset.DownloadSize);
            Assert.That(entries.Single().Included, Is.True, "Existing Crunch 50 must also become 100.");
            Assert.That(SceneNormalMapFormat.Apply(entries, journalPath: journalPath).Failed, Is.Zero);
            settings = Importer(texture).GetPlatformTextureSettings("Standalone");
            Assert.That(settings.format, Is.EqualTo(TextureImporterFormat.DXT5Crunched));
            Assert.That(settings.crunchedCompression, Is.True);
            Assert.That(settings.compressionQuality, Is.EqualTo(100));
            Assert.That(SceneNormalMapFormat.CollectPreset(new[] { material }, SceneNormalMapFormat.Preset.DownloadSize).Single().Changes, Is.Empty);

            entries = SceneNormalMapFormat.CollectPreset(new[] { material }, SceneNormalMapFormat.Preset.Quality);
            Assert.That(SceneNormalMapFormat.Apply(entries, journalPath: journalPath).Failed, Is.Zero);
            settings = Importer(texture).GetPlatformTextureSettings("Standalone");
            Assert.That(settings.format, Is.EqualTo(TextureImporterFormat.BC5));
            Assert.That(settings.crunchedCompression, Is.False);
            Assert.That(SceneNormalMapFormat.CollectPreset(new[] { material }, SceneNormalMapFormat.Preset.Quality).Single().Changes, Is.Empty);
            Assert.That(SceneNormalMapFormat.Restore(journalPath: journalPath).Failed, Is.Zero);
            Assert.That(Desktop(texture), Is.EqualTo(original));
        }
    }
}
