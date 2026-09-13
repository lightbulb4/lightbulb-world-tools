using System;
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
    // Run against an installed, unmodified Mochie v2.13 in the development project.
    // Mochie is an optional integration and is never distributed in the World Tools package.
    public class MochieScenePackerTests
    {
        private Scene previous;
        private Scene scene;
        private string root;

        [SetUp]
        public void SetUp()
        {
            if (Shader.Find("Mochie/Standard") == null) Assert.Ignore("Install Mochie v2.13 to run native packer integration tests.");
            string name = "__LightbulbPackingTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name);
            root = "Assets/" + name;
            previous = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(previous.path))
            {
                if (!Application.isBatchMode) Assert.Ignore("Save the open scene before running the scene-packing tests.");
                Assert.That(EditorSceneManager.SaveScene(previous, root + "/background.unity"), Is.True);
            }
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.That(EditorSceneManager.SaveScene(scene, root + "/active.unity"), Is.True);
            SceneManager.SetActiveScene(scene);
        }

        [TearDown]
        public void TearDown()
        {
            if (root == null) return;
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            Undo.ClearAll();
            Assert.That(root, Does.StartWith("Assets/__LightbulbPackingTests_"));
            AssetDatabase.DeleteAsset(root);
            root = null;
        }

        private Texture2D Texture()
        {
            var image = new Texture2D(16, 16, TextureFormat.RGB24, false, true);
            image.SetPixels(Enumerable.Repeat(new Color(0.4f, 0.4f, 0.4f), 256).ToArray());
            image.Apply();
            string path = AssetDatabase.GenerateUniqueAssetPath(root + "/source.png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private Material Material(string shaderName = "Mochie/Standard")
        {
            var material = new Material(Shader.Find(shaderName));
            AssetDatabase.CreateAsset(material, AssetDatabase.GenerateUniqueAssetPath(root + "/material.mat"));
            new GameObject("Renderer").AddComponent<MeshRenderer>().sharedMaterial = material;
            return material;
        }

        private static Color RawPixel(string path)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try { Assert.That(ImageConversion.LoadImage(texture, File.ReadAllBytes(path)), Is.True); return texture.GetPixel(5, 5); }
            finally { Object.DestroyImmediate(texture); }
        }

        [TestCase("Mochie/Standard")]
        [TestCase("Mochie/Standard Lite")]
        public void NativePackerWritesChannelsSwitchesWorkflowAndUndoRestoresMaterial(string shaderName)
        {
            var adapter = new MochieScenePacker.Adapter();
            Material material = Material(shaderName);
            Texture2D source = Texture();
            foreach (string channel in new[] { "Occlusion", "Roughness", "Metallic", "Height" }) material.SetTexture("_" + channel + "Map", source);
            material.SetFloat("_OcclusionStrength", 0.3f);
            material.SetFloat("_RoughnessStrength", 0.4f);
            material.SetFloat("_MetallicStrength", 0.5f);
            material.SetFloat("_HeightStrength", 0.05f);
            // A second renderer shares the material, but only one output should be created.
            new GameObject("Duplicate use").AddComponent<MeshRenderer>().sharedMaterial = material;
            var preview = MochieScenePacker.Collect(scene, true);
            Assert.That(preview.Entries.Count, Is.EqualTo(1));
            byte[] original = File.ReadAllBytes(AssetDatabase.GetAssetPath(source));
            var result = MochieScenePacker.Apply(preview, adapter);
            Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
            Assert.That(result.Changed, Is.EqualTo(1));
            Assert.That(result.Outputs.Count, Is.EqualTo(1));
            Color packed = RawPixel(result.Outputs.Single());
            Assert.That(packed.r, Is.EqualTo(0.82f).Within(0.01f));
            Assert.That(packed.g, Is.EqualTo(0.16f).Within(0.01f));
            Assert.That(packed.b, Is.EqualTo(0.20f).Within(0.01f));
            Assert.That(packed.a, Is.EqualTo(0.40f).Within(0.01f), "Height strength must not be baked a second time");
            Assert.That(material.GetFloat("_HeightStrength"), Is.EqualTo(0.05f));
            Assert.That(material.GetFloat("_PrimaryWorkflow"), Is.EqualTo(1));
            Assert.That(material.IsKeywordEnabled("_WORKFLOW_PACKED_ON"), Is.True);
            Assert.That(material.GetTexture("_NormalMap"), Is.Null);
            foreach (string channel in new[] { "Occlusion", "Roughness", "Metallic", "Height" })
                Assert.That(material.GetTexture("_" + channel + "Map"), Is.Null);
            Assert.That(result.ClearedReferences, Is.EqualTo(4));
            Assert.That(material.GetFloat("_PackedHeight"), Is.EqualTo(1));
            Assert.That(material.IsKeywordEnabled("_PARALLAX_ON"), Is.True);
            Assert.That(File.ReadAllBytes(AssetDatabase.GetAssetPath(source)), Is.EqualTo(original));
            Assert.That(MochieScenePacker.Collect(scene, true).Entries, Is.Empty);
            Undo.PerformUndo();
            Assert.That(material.GetFloat("_PrimaryWorkflow"), Is.Zero);
            Assert.That(material.GetTexture("_PackedMap"), Is.Null);
            foreach (string channel in new[] { "Occlusion", "Roughness", "Metallic", "Height" })
                Assert.That(material.GetTexture("_" + channel + "Map"), Is.EqualTo(source));
            Assert.That(File.Exists(result.Outputs.Single()), Is.True);
        }

        [Test]
        public void DetailPackingPreservesBlendStrengthAndDisablesAbsentChannels()
        {
            Material material = Material();
            material.SetTexture("_DetailRoughnessMap", Texture());
            material.SetFloat("_DetailRoughnessStrength", 0.3f);
            Assert.That(MochieScenePacker.Collect(scene, false).Entries, Is.Empty);
            var preview = MochieScenePacker.Collect(scene, true);
            Assert.That(preview.Entries.Single().Primary, Is.False);
            var result = MochieScenePacker.Apply(preview, new MochieScenePacker.Adapter());
            Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
            Assert.That(RawPixel(result.Outputs.Single()).g, Is.EqualTo(0.4f).Within(0.01f));
            Assert.That(material.GetFloat("_DetailRoughnessStrength"), Is.EqualTo(0.3f));
            Assert.That(material.GetFloat("_DetailOcclusionStrength"), Is.Zero);
            Assert.That(material.GetFloat("_DetailMetallicStrength"), Is.Zero);
            Assert.That(material.GetFloat("_DetailWorkflow"), Is.EqualTo(1));
            Assert.That(material.IsKeywordEnabled("_WORKFLOW_DETAIL_PACKED_ON"), Is.True);
            Assert.That(material.GetTexture("_DetailRoughnessMap"), Is.Null);
            Assert.That(result.ClearedReferences, Is.EqualTo(1));
        }

        [Test]
        public void StaleMaterialAndSceneChangesRefuseBeforeWritingAndCancellationLeavesMaterialSeparate()
        {
            Material material = Material();
            material.SetTexture("_MetallicMap", Texture());
            var adapter = new MochieScenePacker.Adapter();
            var preview = MochieScenePacker.Collect(scene, true);
            material.SetFloat("_MetallicStrength", 0.37f);
            Assert.Throws<InvalidOperationException>(() => MochieScenePacker.Apply(preview, adapter));
            preview = MochieScenePacker.Collect(scene, true);
            SceneManager.SetActiveScene(previous);
            Assert.Throws<InvalidOperationException>(() => MochieScenePacker.Apply(preview, adapter));
            SceneManager.SetActiveScene(scene);
            Assert.That(MochieScenePacker.Apply(preview, adapter, _ => true).Cancelled, Is.True);
            Assert.That(material.GetFloat("_PrimaryWorkflow"), Is.Zero);
            Assert.That(Directory.GetFiles(root, "*.png").Length, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MatchingInputsShareOneOutputAndPreserveIndependentAreaLitAndRuntimeStrengths(bool detail)
        {
            Material a = Material();
            Material b = Material(detail ? "Mochie/Standard" : "Mochie/Standard Lite");
            string prefix = detail ? "_Detail" : "_";
            Texture2D source = Texture();
            foreach (Material material in new[] { a, b })
            {
                material.SetTexture(prefix + "RoughnessMap", source);
                material.SetTextureScale(prefix + "PackedMap", new Vector2(3, 4));
                material.SetTextureOffset(prefix + "PackedMap", new Vector2(0.2f, 0.3f));
            }
            a.SetTextureOffset("_AreaLitOcclusion", new Vector2(0.1f, 0.2f));
            b.SetTextureOffset("_AreaLitOcclusion", new Vector2(0.7f, 0.8f));
            a.SetTextureScale("_AreaLitOcclusion", new Vector2(0.25f, 0.25f));
            b.SetTextureScale("_AreaLitOcclusion", new Vector2(0.5f, 0.5f));
            string strength = detail ? "_DetailRoughnessStrength" : "_HeightStrength";
            a.SetFloat(strength, 0.1f);
            b.SetFloat(strength, 0.8f);
            string beforeA = EditorJsonUtility.ToJson(a);
            string beforeB = EditorJsonUtility.ToJson(b);
            var result = MochieScenePacker.Apply(MochieScenePacker.Collect(scene, true), new MochieScenePacker.Adapter());
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Changed, Is.EqualTo(2));
            Assert.That(result.Outputs.Count, Is.EqualTo(1));
            Assert.That(result.Reused, Is.EqualTo(1));
            Assert.That(result.ClearedReferences, Is.EqualTo(2));
            Assert.That(a.GetTexture(prefix + "RoughnessMap"), Is.Null);
            Assert.That(b.GetTexture(prefix + "RoughnessMap"), Is.Null);
            Assert.That(a.GetTexture(prefix + "PackedMap"), Is.SameAs(b.GetTexture(prefix + "PackedMap")));
            Assert.That(b.GetTextureScale(prefix + "PackedMap"), Is.EqualTo(Vector2.one));
            Assert.That(b.GetTextureOffset(prefix + "PackedMap"), Is.EqualTo(Vector2.zero));
            Assert.That(a.GetTextureOffset("_AreaLitOcclusion"), Is.EqualTo(new Vector2(0.1f, 0.2f)));
            Assert.That(b.GetTextureOffset("_AreaLitOcclusion"), Is.EqualTo(new Vector2(0.7f, 0.8f)));
            Assert.That(b.GetTextureScale("_AreaLitOcclusion"), Is.EqualTo(new Vector2(0.5f, 0.5f)));
            Assert.That(a.GetFloat(strength), Is.EqualTo(0.1f));
            Assert.That(b.GetFloat(strength), Is.EqualTo(0.8f));
            Assert.That(b.IsKeywordEnabled(detail ? "_WORKFLOW_DETAIL_PACKED_ON" : "_WORKFLOW_PACKED_ON"), Is.True);
            Undo.PerformUndo();
            Assert.That(EditorJsonUtility.ToJson(a), Is.EqualTo(beforeA));
            Assert.That(EditorJsonUtility.ToJson(b), Is.EqualTo(beforeB));
            Assert.That(File.Exists(result.Outputs.Single()), Is.True);
        }

        [TestCase("strength")]
        [TestCase("offset")]
        [TestCase("scale")]
        [TestCase("source")]
        [TestCase("height")]
        public void DifferentPackingInputsProduceSeparateOutputs(string difference)
        {
            Material a = Material();
            Material b = Material();
            var source = Texture();
            a.SetTexture("_RoughnessMap", source);
            b.SetTexture("_RoughnessMap", source);
            if (difference == "strength") b.SetFloat("_RoughnessStrength", 0.37f);
            if (difference == "offset") b.SetTextureOffset("_RoughnessMap", new Vector2(0.2f, 0.3f));
            if (difference == "scale") b.SetTextureScale("_RoughnessMap", new Vector2(2, 3));
            if (difference == "source") b.SetTexture("_RoughnessMap", Texture());
            if (difference == "height") b.SetTexture("_HeightMap", source);
            var result = MochieScenePacker.Apply(MochieScenePacker.Collect(scene, true), new MochieScenePacker.Adapter());
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Outputs.Count, Is.EqualTo(2));
            Assert.That(result.Reused, Is.Zero);
            Assert.That(a.GetTexture("_PackedMap"), Is.Not.EqualTo(b.GetTexture("_PackedMap")));
        }

        private Texture2D CopyTexture(Texture2D source)
        {
            string path = AssetDatabase.GenerateUniqueAssetPath(root + "/z_copy.png");
            Assert.That(AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(source), path), Is.True);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private Material Packed(Texture2D texture, bool detail = false)
        {
            Material material = Material();
            material.SetTexture(detail ? "_DetailPackedMap" : "_PackedMap", texture);
            material.SetFloat(detail ? "_DetailWorkflow" : "_PrimaryWorkflow", 1);
            return material;
        }

        [Test]
        public void ConsolidationOnlyChangesScenePackedReferencesAndUndoRestoresThem()
        {
            Texture2D first = Texture();
            Texture2D copy = CopyTexture(first);
            Material a = Packed(first);
            Material b = Packed(copy, true);
            b.SetTextureOffset("_AreaLitOcclusion", new Vector2(0.7f, 0.8f));
            b.SetTextureOffset("_DetailPackedMap", new Vector2(0.4f, 0.5f));
            b.SetTextureScale("_DetailPackedMap", new Vector2(2, 3));
            b.SetFloat("_DetailRoughnessStrength", 0.37f);
            // A separate-workflow reference must stay untouched even when its texture matches.
            Material separate = Material();
            separate.SetTexture("_PackedMap", copy);
            var outside = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(outside);
            Material outsideMaterial = Packed(copy);
            SceneManager.SetActiveScene(scene);
            try
            {
                string stateA = EditorJsonUtility.ToJson(a);
                string stateB = EditorJsonUtility.ToJson(b);
                string stateSeparate = EditorJsonUtility.ToJson(separate);
                string stateOutside = EditorJsonUtility.ToJson(outsideMaterial);
                var preview = MochiePackedMapDuplicates.Collect(scene);
                Assert.That(preview.Notes, Is.Empty, string.Join("\n", preview.Notes));
                Assert.That(preview.Groups.Count, Is.EqualTo(1));
                Texture2D keep = preview.Groups.Single().Keep;
                Assert.That(AssetDatabase.GetAssetPath(keep), Is.EqualTo(new[] { first, copy }.Select(AssetDatabase.GetAssetPath).OrderBy(p => p, StringComparer.Ordinal).First()));
                Assert.That(MochiePackedMapDuplicates.Apply(preview), Is.EqualTo(1));
                Assert.That(a.GetTexture("_PackedMap"), Is.SameAs(b.GetTexture("_DetailPackedMap")));
                Assert.That(b.GetTextureOffset("_AreaLitOcclusion"), Is.EqualTo(new Vector2(0.7f, 0.8f)));
                Assert.That(b.GetTextureOffset("_DetailPackedMap"), Is.EqualTo(new Vector2(0.4f, 0.5f)));
                Assert.That(b.GetTextureScale("_DetailPackedMap"), Is.EqualTo(new Vector2(2, 3)));
                Assert.That(b.GetFloat("_DetailRoughnessStrength"), Is.EqualTo(0.37f));
                Assert.That(EditorJsonUtility.ToJson(separate), Is.EqualTo(stateSeparate));
                Assert.That(EditorJsonUtility.ToJson(outsideMaterial), Is.EqualTo(stateOutside));
                Assert.That(MochiePackedMapDuplicates.Collect(scene).Groups, Is.Empty);
                Undo.PerformUndo();
                Assert.That(EditorJsonUtility.ToJson(a), Is.EqualTo(stateA));
                Assert.That(EditorJsonUtility.ToJson(b), Is.EqualTo(stateB));
                Assert.That(File.Exists(AssetDatabase.GetAssetPath(first)) && File.Exists(AssetDatabase.GetAssetPath(copy)), Is.True);
            }
            finally { EditorSceneManager.CloseScene(outside, true); }
        }

        [TestCase("srgb")]
        [TestCase("filter")]
        [TestCase("wrap")]
        [TestCase("mipmaps")]
        [TestCase("size")]
        [TestCase("compression")]
        [TestCase("platform")]
        public void ConsolidationRefusesDifferentImportSettings(string difference)
        {
            Texture2D first = Texture();
            Texture2D copy = CopyTexture(first);
            Packed(first);
            Packed(copy);
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(copy));
            if (difference == "srgb") importer.sRGBTexture = true;
            if (difference == "filter") importer.filterMode = FilterMode.Point;
            if (difference == "wrap") importer.wrapMode = TextureWrapMode.Clamp;
            if (difference == "mipmaps") importer.mipmapEnabled = !importer.mipmapEnabled;
            if (difference == "size") importer.maxTextureSize = 32;
            if (difference == "compression") importer.textureCompression = TextureImporterCompression.Compressed;
            if (difference == "platform")
            {
                var settings = importer.GetPlatformTextureSettings("Android");
                settings.overridden = true;
                settings.maxTextureSize = 32;
                importer.SetPlatformTextureSettings(settings);
            }
            importer.SaveAndReimport();
            Assert.That(MochiePackedMapDuplicates.Collect(scene).Groups, Is.Empty);
        }

        [TestCase("material")]
        [TestCase("texture")]
        [TestCase("scene")]
        public void ConsolidationStalePreviewRefusesBeforeChangingAnyReferences(string change)
        {
            Texture2D first = Texture();
            Texture2D copy = CopyTexture(first);
            Material a = Packed(first);
            Material b = Packed(copy);
            var preview = MochiePackedMapDuplicates.Collect(scene);
            Assert.That(preview.Groups.Count, Is.EqualTo(1));
            if (change == "material") preview.Groups.Single().Replacements[0].Material.SetFloat("_AreaLitOcclusionUVSet", 2);
            if (change == "texture")
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(copy));
                importer.filterMode = FilterMode.Point;
                importer.SaveAndReimport();
            }
            if (change == "scene") SceneManager.SetActiveScene(previous);
            Assert.Throws<InvalidOperationException>(() => MochiePackedMapDuplicates.Apply(preview));
            Assert.That(a.GetTexture("_PackedMap"), Is.EqualTo(first));
            Assert.That(b.GetTexture("_PackedMap"), Is.EqualTo(copy));
            SceneManager.SetActiveScene(scene);
        }

        [Test]
        public void DifferentPixelsAreNotConsolidatedAndExcludedGroupsAreUntouched()
        {
            Texture2D first = Texture();
            Texture2D copy = CopyTexture(first);
            Material a = Packed(first);
            Material b = Packed(copy);
            var preview = MochiePackedMapDuplicates.Collect(scene);
            preview.Groups.Single().Included = false;
            Assert.That(MochiePackedMapDuplicates.Apply(preview), Is.Zero);
            Assert.That(a.GetTexture("_PackedMap"), Is.EqualTo(first));
            Assert.That(b.GetTexture("_PackedMap"), Is.EqualTo(copy));
            var image = new Texture2D(16, 16, TextureFormat.RGB24, false, true);
            image.SetPixels(Enumerable.Repeat(new Color(0.4f, 0.4f, 0.4f), 256).ToArray());
            image.SetPixel(0, 0, Color.black);
            image.Apply();
            File.WriteAllBytes(AssetDatabase.GetAssetPath(copy), image.EncodeToPNG());
            Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(copy), ImportAssetOptions.ForceSynchronousImport);
            Assert.That(MochiePackedMapDuplicates.Collect(scene).Groups, Is.Empty, "One differing pixel must prevent consolidation.");
            Assert.Throws<OperationCanceledException>(() => MochiePackedMapDuplicates.Collect(scene, _ => true));
        }

        [Test]
        public void ConsolidationRepairsDuplicatesFromSeparateNativePackingRuns()
        {
            Texture2D source = Texture();
            Material a = Material();
            a.SetTexture("_MetallicMap", source);
            var adapter = new MochieScenePacker.Adapter();
            var first = MochieScenePacker.Apply(MochieScenePacker.Collect(scene, false), adapter);
            Assert.That(first.Errors, Is.Empty);
            Material b = Material();
            b.SetTexture("_MetallicMap", source);
            b.SetTextureOffset("_AreaLitOcclusion", new Vector2(0.7f, 0.8f));
            var second = MochieScenePacker.Apply(MochieScenePacker.Collect(scene, false), adapter);
            Assert.That(second.Errors, Is.Empty);
            Assert.That(a.GetTexture("_PackedMap"), Is.Not.EqualTo(b.GetTexture("_PackedMap")));
            var preview = MochiePackedMapDuplicates.Collect(scene);
            Assert.That(preview.Groups.Count, Is.EqualTo(1));
            Assert.That(MochiePackedMapDuplicates.Apply(preview), Is.EqualTo(1));
            Assert.That(a.GetTexture("_PackedMap"), Is.EqualTo(b.GetTexture("_PackedMap")));
            Assert.That(b.GetTextureOffset("_AreaLitOcclusion"), Is.EqualTo(new Vector2(0.7f, 0.8f)));
            foreach (string path in first.Outputs.Concat(second.Outputs)) Assert.That(File.Exists(path), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExistingPackedCleanupPreservesSettingsFilesAndUndoWithoutRepacking(bool detail)
        {
            Texture2D source = Texture();
            Material material = Packed(source, detail);
            material.EnableKeyword(detail ? "_WORKFLOW_DETAIL_PACKED_ON" : "_WORKFLOW_PACKED_ON");
            string prefix = detail ? "_Detail" : "_";
            string[] channels = detail ? new[] { "Occlusion", "Roughness", "Metallic" } : new[] { "Occlusion", "Roughness", "Metallic", "Height" };
            foreach (string channel in channels) material.SetTexture(prefix + channel + "Map", source);
            material.SetTexture("_AreaLitOcclusion", source);
            material.SetTextureOffset("_AreaLitOcclusion", new Vector2(0.7f, 0.8f));
            material.SetTexture("_NormalMap", source);
            material.SetTexture("_DetailNormalMap", source);
            material.SetTexture("_HeightMask", source);
            material.SetTexture("_MainTex", source);
            material.SetTextureScale(prefix + "PackedMap", new Vector2(2, 3));
            material.SetTextureOffset(prefix + "PackedMap", new Vector2(0.2f, 0.4f));
            material.SetFloat(prefix + "MetallicChannel", 1);
            material.SetFloat("_PackedHeight", 1);
            material.SetFloat("_HeightStrength", 0.05f);
            material.SetFloat("_DetailRoughnessStrength", 0.37f);
            string before = EditorJsonUtility.ToJson(material);
            byte[] original = File.ReadAllBytes(AssetDatabase.GetAssetPath(source));
            var preview = MochieScenePacker.Collect(scene, true);
            var entry = preview.Entries.Single();
            Assert.That(entry.Primary || entry.Detail, Is.False);
            Assert.That(detail ? entry.CleanupDetail : entry.CleanupPrimary, Is.True);
            var result = MochieScenePacker.Apply(preview, new MochieScenePacker.Adapter());
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Changed, Is.EqualTo(1));
            Assert.That(result.ClearedReferences, Is.EqualTo(channels.Length));
            Assert.That(result.Outputs, Is.Empty);
            foreach (string channel in channels) Assert.That(material.GetTexture(prefix + channel + "Map"), Is.Null);
            Assert.That(material.GetTexture(prefix + "PackedMap"), Is.EqualTo(source));
            Assert.That(material.GetTextureScale(prefix + "PackedMap"), Is.EqualTo(new Vector2(2, 3)));
            Assert.That(material.GetTextureOffset(prefix + "PackedMap"), Is.EqualTo(new Vector2(0.2f, 0.4f)));
            Assert.That(material.GetFloat(prefix + "MetallicChannel"), Is.EqualTo(1));
            Assert.That(material.GetFloat("_PackedHeight"), Is.EqualTo(1));
            Assert.That(material.GetFloat("_HeightStrength"), Is.EqualTo(0.05f));
            Assert.That(material.GetFloat("_DetailRoughnessStrength"), Is.EqualTo(0.37f));
            Assert.That(material.GetTextureOffset("_AreaLitOcclusion"), Is.EqualTo(new Vector2(0.7f, 0.8f)));
            foreach (string property in new[] { "_AreaLitOcclusion", "_NormalMap", "_DetailNormalMap", "_HeightMask", "_MainTex" })
                Assert.That(material.GetTexture(property), Is.EqualTo(source));
            Assert.That(File.ReadAllBytes(AssetDatabase.GetAssetPath(source)), Is.EqualTo(original));
            Assert.That(MochieScenePacker.Collect(scene, true).Entries, Is.Empty);
            Undo.PerformUndo();
            Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(before));
        }

        [TestCase("missing_texture")]
        [TestCase("missing_keyword")]
        [TestCase("detail_excluded")]
        public void ExistingPackedCleanupRefusesUnsafeOrExcludedWorkflows(string condition)
        {
            bool detail = condition == "detail_excluded";
            Texture2D source = Texture();
            Material material = Packed(source, detail);
            string prefix = detail ? "_Detail" : "_";
            material.SetTexture(prefix + "RoughnessMap", source);
            if (condition != "missing_keyword") material.EnableKeyword(detail ? "_WORKFLOW_DETAIL_PACKED_ON" : "_WORKFLOW_PACKED_ON");
            if (condition == "missing_texture") material.SetTexture(prefix + "PackedMap", null);
            var preview = MochieScenePacker.Collect(scene, !detail);
            Assert.That(preview.Entries, Is.Empty);
            Assert.That(material.GetTexture(prefix + "RoughnessMap"), Is.EqualTo(source));
        }

        [Test]
        public void ExistingPackedCleanupRejectsChangedPackedTextureAndHonorsCancellationAndExclusion()
        {
            Texture2D source = Texture();
            Material material = Packed(source);
            material.EnableKeyword("_WORKFLOW_PACKED_ON");
            material.SetTexture("_RoughnessMap", source);
            var adapter = new MochieScenePacker.Adapter();
            var preview = MochieScenePacker.Collect(scene, false);
            preview.Entries.Single().Included = false;
            Assert.That(MochieScenePacker.Apply(preview, adapter).Changed, Is.Zero);
            preview.Entries.Single().Included = true;
            Assert.That(MochieScenePacker.Apply(preview, adapter, _ => true).Cancelled, Is.True);
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source));
            importer.filterMode = FilterMode.Point;
            importer.SaveAndReimport();
            Assert.Throws<InvalidOperationException>(() => MochieScenePacker.Apply(preview, adapter));
            Assert.That(material.GetTexture("_RoughnessMap"), Is.EqualTo(source));
        }

        [Test]
        public void FailureAfterPrimaryPackRestoresMaterialAndKeepsSourceReferences()
        {
            Material material = Material();
            Texture2D source = Texture();
            material.SetTexture("_RoughnessMap", source);
            string before = EditorJsonUtility.ToJson(material);
            var preview = MochieScenePacker.Collect(scene, false);
            // Force the second operation to fail eligibility after the real primary pack has written its PNG.
            preview.Entries.Single().Detail = true;
            var result = MochieScenePacker.Apply(preview, new MochieScenePacker.Adapter());
            Assert.That(result.Errors.Count, Is.EqualTo(1));
            Assert.That(result.Changed, Is.Zero);
            Assert.That(result.ClearedReferences, Is.Zero);
            Assert.That(result.Outputs.Count, Is.EqualTo(1));
            Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(before));
            Assert.That(File.Exists(AssetDatabase.GetAssetPath(source)), Is.True);
        }
    }
}
