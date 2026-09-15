using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools.Tests
{
    public class MaterialTextureBatchTests
    {
        private string root;
        private readonly List<Object> temporary = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            string name = "__LightbulbTextureTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name);
            root = "Assets/" + name;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object value in temporary) if (value != null) Object.DestroyImmediate(value);
            temporary.Clear();
            Assert.That(root, Does.StartWith("Assets/__LightbulbTextureTests_"));
            AssetDatabase.DeleteAsset(root);
        }

        private Texture2D Texture(string name, int width, int cap = 2048)
        {
            var image = new Texture2D(width, 32, TextureFormat.RGBA32, false);
            File.WriteAllBytes(root + "/" + name + ".png", image.EncodeToPNG());
            Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(root + "/" + name + ".png", ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(root + "/" + name + ".png");
            importer.maxTextureSize = cap;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(importer.assetPath);
        }

        private Material Material(Texture texture)
        {
            var material = new Material(Shader.Find("Standard"));
            temporary.Add(material);
            material.SetTexture("_MainTex", texture);
            return material;
        }

        private static TextureImporter Importer(Texture texture) => (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));

        [Test]
        public void DeduplicatesSlotsAndMaterialsAndDoesNotChangePreviewedAssets()
        {
            Texture2D texture = Texture("shared", 2048);
            Material first = Material(texture);
            Material second = Material(texture);
            first.SetTexture("_BumpMap", texture);
            byte[] original = File.ReadAllBytes(AssetDatabase.GetAssetPath(texture) + ".meta");
            var entries = MaterialTextureBatch.Collect(new[] { first, second, first }, 1024, MaterialTextureBatch.CrunchMode.LeaveUnchanged);
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].Uses.Count, Is.EqualTo(3));
            Assert.That(entries[0].Changes.Single().maxTextureSize, Is.EqualTo(1024));
            Assert.That(File.ReadAllBytes(entries[0].Path + ".meta"), Is.EqualTo(original));
        }

        [TestCase(512, 2048)]
        [TestCase(1024, 2048)]
        [TestCase(2048, 512)]
        [TestCase(2048, 1024)]
        public void LeavesSmallSourcesAndExistingLowerCapsAlone(int width, int cap)
        {
            var entry = MaterialTextureBatch.Collect(new[] { Material(Texture("small", width, cap)) },
                1024, MaterialTextureBatch.CrunchMode.LeaveUnchanged).Single();
            Assert.That(entry.Changes, Is.Empty);
        }

        [Test]
        public void UpdatesOnlyLargerPlatformCapsAndPreservesOtherSettingsAndBacksUpMetadata()
        {
            Texture2D texture = Texture("platforms", 2048);
            TextureImporter importer = Importer(texture);
            importer.sRGBTexture = false;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            var windows = importer.GetPlatformTextureSettings("Standalone");
            windows.overridden = true;
            windows.maxTextureSize = 2048;
            windows.format = TextureImporterFormat.BC7;
            importer.SetPlatformTextureSettings(windows);
            var android = importer.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 512;
            importer.SetPlatformTextureSettings(android);
            importer.SaveAndReimport();
            byte[] metadata = File.ReadAllBytes(importer.assetPath + ".meta");
            byte[] source = File.ReadAllBytes(importer.assetPath);
            Material material = Material(texture);
            var entries = MaterialTextureBatch.Collect(new[] { material }, 1024, MaterialTextureBatch.CrunchMode.LeaveUnchanged);
            Assert.That(entries.Single().Changes.Count, Is.EqualTo(2));
            var result = MaterialTextureBatch.Apply(entries);
            Assert.That(result.Changed, Is.EqualTo(1));
            Assert.That(result.Failed, Is.Zero);
            Assert.That(File.ReadAllBytes(Path.Combine(result.BackupRoot, importer.assetPath + ".meta")), Is.EqualTo(metadata));
            Assert.That(File.ReadAllBytes(importer.assetPath), Is.EqualTo(source));
            importer = Importer(texture);
            Assert.That(importer.maxTextureSize, Is.EqualTo(1024));
            Assert.That(importer.GetPlatformTextureSettings("Standalone").maxTextureSize, Is.EqualTo(1024));
            Assert.That(importer.GetPlatformTextureSettings("Standalone").format, Is.EqualTo(TextureImporterFormat.BC7));
            Assert.That(importer.GetPlatformTextureSettings("Android").maxTextureSize, Is.EqualTo(512));
            Assert.That(importer.GetPlatformTextureSettings("iPhone").overridden, Is.False);
            Assert.That(importer.sRGBTexture, Is.False);
            Assert.That(importer.mipmapEnabled, Is.False);
            Assert.That(importer.wrapMode, Is.EqualTo(TextureWrapMode.Clamp));
            Assert.That(texture.width, Is.EqualTo(1024));
            Assert.That(MaterialTextureBatch.Collect(new[] { material }, 1024,
                MaterialTextureBatch.CrunchMode.LeaveUnchanged).Single().Changes, Is.Empty);

            File.Copy(Path.Combine(result.BackupRoot, importer.assetPath + ".meta"), importer.assetPath + ".meta", true);
            AssetDatabase.ImportAsset(importer.assetPath, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(Importer(texture).maxTextureSize, Is.EqualTo(2048), "Backup restores original settings");
        }

        [Test]
        public void CrunchCanChangeSmallTexturesAndExplicitFormatsRoundTrip()
        {
            Texture2D texture = Texture("crunch", 128);
            TextureImporter importer = Importer(texture);
            var settings = importer.GetPlatformTextureSettings("Standalone");
            settings.overridden = true;
            settings.format = TextureImporterFormat.DXT5;
            importer.SetPlatformTextureSettings(settings);
            importer.SaveAndReimport();
            Material material = Material(texture);
            foreach (var mode in new[] { MaterialTextureBatch.CrunchMode.Enable, MaterialTextureBatch.CrunchMode.Disable })
            {
                var result = MaterialTextureBatch.Apply(MaterialTextureBatch.Collect(new[] { material }, 1024, mode));
                Assert.That(result.Changed, Is.EqualTo(1));
                Assert.That(result.Failed, Is.Zero);
                importer = Importer(texture);
                bool enabled = mode == MaterialTextureBatch.CrunchMode.Enable;
                Assert.That(importer.crunchedCompression, Is.EqualTo(enabled));
                Assert.That(importer.maxTextureSize, Is.EqualTo(2048));
                Assert.That(importer.GetPlatformTextureSettings("Standalone").format,
                    Is.EqualTo(enabled ? TextureImporterFormat.DXT5Crunched : TextureImporterFormat.DXT5));
                Assert.That(texture.width, Is.EqualTo(128));
            }
        }

        [TestCase(TextureImporterFormat.BC7)]
        [TestCase(TextureImporterFormat.RGBA32)]
        [TestCase(TextureImporterFormat.ASTC_6x6)]
        public void UnsupportedExplicitFormatsAreNotConvertedForCrunch(TextureImporterFormat format)
        {
            var settings = new TextureImporterPlatformSettings { format = format };
            var notes = new List<string>();
            Assert.That(MaterialTextureBatch.PlanCrunch(settings, MaterialTextureBatch.CrunchMode.Enable,
                false, notes, "Test"), Is.False);
            Assert.That(settings.format, Is.EqualTo(format));
            Assert.That(settings.crunchedCompression, Is.False);
            Assert.That(notes.Single(), Does.Contain("unsupported"));
        }

        [Test]
        public void CrunchOffFilterPreservesMixedExistingQualitiesAndOnlyEnablesMissingCrunch()
        {
            Texture2D low = Texture("quality50", 32), high = Texture("quality100", 32), off = Texture("off", 32);
            foreach (var pair in new[] { (low, 50), (high, 100) })
            {
                TextureImporter importer = Importer(pair.Item1);
                importer.crunchedCompression = true;
                importer.compressionQuality = pair.Item2;
                importer.SaveAndReimport();
            }
            byte[] lowMetadata = File.ReadAllBytes(AssetDatabase.GetAssetPath(low) + ".meta");
            byte[] highMetadata = File.ReadAllBytes(AssetDatabase.GetAssetPath(high) + ".meta");
            var entries = MaterialTextureBatch.CollectTextures(new[] { low, high, off }, MaterialTextureBatch.CrunchMode.Enable, 73, true);
            Assert.That(entries.Where(e => e.Included).Select(e => e.Texture), Is.EquivalentTo(new[] { off }));
            Assert.That(entries.Count(e => e.ExcludedByCrunchFilter), Is.EqualTo(2));
            var result = MaterialTextureBatch.Apply(entries);
            Assert.That(result.Changed, Is.EqualTo(1));
            Assert.That(result.Failed, Is.Zero);
            Assert.That(Importer(off).crunchedCompression, Is.True);
            Assert.That(Importer(off).compressionQuality, Is.EqualTo(73));
            Assert.That(File.ReadAllBytes(AssetDatabase.GetAssetPath(low) + ".meta"), Is.EqualTo(lowMetadata));
            Assert.That(File.ReadAllBytes(AssetDatabase.GetAssetPath(high) + ".meta"), Is.EqualTo(highMetadata));
            Assert.That(MaterialTextureBatch.CollectTextures(new[] { low, high }, MaterialTextureBatch.CrunchMode.Enable, 73)
                .Count(e => e.Included), Is.EqualTo(2), "Without the filter, quality updates are still offered.");
        }

        [TestCase(false, 50, "Crunch OFF → ON · Quality: 100")]
        [TestCase(false, 100, "Crunch OFF → ON · Quality: 100")]
        [TestCase(true, 50, "Crunch stays ON · Quality: 50 → 100")]
        public void CrunchPreviewDistinguishesInactiveQualityFromActiveQuality(bool enabled, int storedQuality, string expected)
        {
            var settings = new TextureImporterPlatformSettings
            {
                format = TextureImporterFormat.Automatic,
                textureCompression = TextureImporterCompression.Compressed,
                crunchedCompression = enabled,
                compressionQuality = storedQuality
            };
            var notes = new List<string>();
            Assert.That(MaterialTextureBatch.PlanCrunch(settings, MaterialTextureBatch.CrunchMode.Enable, false, notes, "Default platform", 100), Is.True);
            Assert.That(notes, Has.Count.EqualTo(1));
            Assert.That(notes.Single(), Does.StartWith("Default platform: " + expected));
            if (!enabled) Assert.That(notes.Single(), Does.Not.Contain("50"), "Inactive stored quality must not look like active compression.");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void CrunchOffFilterChecksOnlyEnabledPlatformOverrides(bool enabledOverride)
        {
            Texture2D texture = Texture("override", 32);
            TextureImporter importer = Importer(texture);
            importer.crunchedCompression = false;
            var settings = importer.GetPlatformTextureSettings("Standalone");
            settings.overridden = enabledOverride;
            settings.format = TextureImporterFormat.DXT5Crunched;
            settings.crunchedCompression = true;
            importer.SetPlatformTextureSettings(settings);
            importer.SaveAndReimport();
            var entry = MaterialTextureBatch.CollectTextures(new[] { texture }, MaterialTextureBatch.CrunchMode.Enable, 73, true).Single();
            Assert.That(entry.ExcludedByCrunchFilter, Is.EqualTo(enabledOverride));
            Assert.That(entry.Included, Is.EqualTo(!enabledOverride));
        }

        [Test]
        public void ExclusionsCancellationAndStalePreviewDoNotModifyTextures()
        {
            Texture2D first = Texture("one", 2048);
            Texture2D second = Texture("two", 2048);
            Material[] materials = { Material(first), Material(second) };
            var entries = MaterialTextureBatch.Collect(materials, 1024, MaterialTextureBatch.CrunchMode.LeaveUnchanged);
            entries[0].Included = false;
            var cancelled = MaterialTextureBatch.Apply(entries, (i, count, path) => true);
            Assert.That(cancelled.Cancelled, Is.True);
            Assert.That(cancelled.Changed, Is.Zero);
            Assert.That(Importer(first).maxTextureSize, Is.EqualTo(2048));
            var applied = MaterialTextureBatch.Apply(entries);
            Assert.That(applied.Changed, Is.EqualTo(1));
            Assert.That(Importer(first).maxTextureSize, Is.EqualTo(2048));
            Assert.That(Importer(second).maxTextureSize, Is.EqualTo(1024));
            entries = MaterialTextureBatch.Collect(materials, 1024, MaterialTextureBatch.CrunchMode.LeaveUnchanged);
            TextureImporter importer = Importer(first);
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
            Assert.Throws<InvalidOperationException>(() => MaterialTextureBatch.Apply(entries));
            Assert.That(Importer(first).maxTextureSize, Is.EqualTo(2048));
        }

        [Test]
        public void SkipsGeneratedTexturesAndReadOnlyMetadataButIncludesNormals()
        {
            var generated = new Texture2D(8, 8);
            temporary.Add(generated);
            Texture2D normal = Texture("normal", 2048);
            TextureImporter importer = Importer(normal);
            importer.textureType = TextureImporterType.NormalMap;
            importer.SaveAndReimport();
            var entries = MaterialTextureBatch.Collect(new[] { Material(generated), Material(normal) },
                1024, MaterialTextureBatch.CrunchMode.LeaveUnchanged);
            Assert.That(entries.Count(e => e.Changes.Count > 0), Is.EqualTo(1));
            string meta = importer.assetPath + ".meta";
            FileAttributes attributes = File.GetAttributes(meta);
            try
            {
                File.SetAttributes(meta, attributes | FileAttributes.ReadOnly);
                Assert.That(MaterialTextureBatch.Collect(new[] { Material(normal) }, 1024,
                    MaterialTextureBatch.CrunchMode.LeaveUnchanged).Single().Changes, Is.Empty);
            }
            finally { File.SetAttributes(meta, attributes); }
        }

        [Test]
        public void RegisteredMenuOpensWithMultipleSelectedMaterials()
        {
            Object[] previous = Selection.objects;
            try
            {
                Selection.objects = new Object[] { Material(Texture("menu", 2048)), Material(Texture("menuSmall", 128)) };
                Assert.That(EditorApplication.ExecuteMenuItem("Tools/Lightbulb/Resize Referenced Textures..."), Is.True);
                Assert.That(Resources.FindObjectsOfTypeAll<MaterialTextureBatchWindow>(), Has.Length.EqualTo(1));
            }
            finally
            {
                foreach (var window in Resources.FindObjectsOfTypeAll<MaterialTextureBatchWindow>()) window.Close();
                Selection.objects = previous;
            }
        }
    }
}
