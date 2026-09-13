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
    public class EmptyMaterialMapsTests
    {
        private string root;

        [SetUp]
        public void SetUp()
        {
            string name = "__LightbulbEmptyMapTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name);
            root = "Assets/" + name;
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            Assert.That(root, Does.StartWith("Assets/__LightbulbEmptyMapTests_"));
            AssetDatabase.DeleteAsset(root);
        }

        private static EmptyMaterialMaps.Analysis Analyze(Color[] pixels, double threshold) =>
            EmptyMaterialMaps.Analyze(() => new[] { pixels }, threshold);

        [TestCase(0f)]
        [TestCase(1f)]
        [TestCase(0.42f)]
        public void ExactFindsAnyConstantValue(float value)
        {
            var analysis = Analyze(Enumerable.Repeat(new Color(value, value, value, 1), 100).ToArray(), 100);
            Assert.That(analysis.Qualifies(100), Is.True);
        }

        [Test]
        public void FuzzyHonorsExactBoundaryAndFirstPixelOutlier()
        {
            var pixels = Enumerable.Repeat(Color.black, 100).ToArray();
            pixels[0] = Color.white;
            var analysis = Analyze(pixels, 99);
            Assert.That(analysis.Qualifies(99), Is.True);
            Assert.That(analysis.Qualifies(100), Is.False);
            pixels[99] = Color.white;
            Assert.That(Analyze(pixels, 99).Qualifies(99), Is.False);
        }

        [Test]
        public void AlphaDataAndSmallGradientsAreNotEmpty()
        {
            var alpha = Enumerable.Range(0, 100).Select(i => new Color(1, 1, 1, i / 100f)).ToArray();
            Assert.That(Analyze(alpha, 99).Qualifies(99), Is.False);
            var subtle = Enumerable.Range(0, 100).Select(i => new Color(0.5f + i * 0.000001f, 0, 0, 1)).ToArray();
            Assert.That(Analyze(subtle, 99).Qualifies(99), Is.False);
        }

        [TestCase("_MetallicGlossMap", "Metallic")]
        [TestCase("_DetailRoughnessMap", "Roughness / smoothness")]
        [TestCase("_AOMap", "AO")]
        [TestCase("_BumpMap", "Normal")]
        [TestCase("_DetailNormalMap", "Normal")]
        [TestCase("_ParallaxMap", "Height")]
        [TestCase("_PackedMap", "Packed data")]
        [TestCase("_ORM", "Packed data")]
        [TestCase("_MainTex", null)]
        public void RecognizesCommonMapSlots(string property, string expected)
        {
            Assert.That(EmptyMaterialMaps.Classify(property), Is.EqualTo(expected));
            Assert.That(EmptyMaterialMaps.Classify("_Custom", "Ambient Occlusion"), Is.EqualTo("AO"));
        }

        private Texture2D Image(string name, Color[] pixels, int width, int height, bool normal = false, bool compressed = false)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            texture.SetPixels(pixels);
            texture.Apply();
            string path = root + "/" + name + ".png";
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.isReadable = false;
            importer.mipmapEnabled = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = compressed ? TextureImporterCompression.Compressed : TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private Material Material(string name, Texture texture)
        {
            var material = new Material(Shader.Find("Standard"));
            material.SetTexture("_MetallicGlossMap", texture);
            AssetDatabase.CreateAsset(material, root + "/" + name + ".mat");
            return material;
        }

        [Test]
        public void GpuReadsEveryPixelIncludingLastPartialStripWithoutChangingImporter()
        {
            var pixels = Enumerable.Repeat(Color.black, 32 * 257).ToArray();
            pixels[pixels.Length - 1] = Color.white;
            Texture2D texture = Image("one-pixel", pixels, 32, 257);
            byte[] metadata = File.ReadAllBytes(AssetDatabase.GetAssetPath(texture) + ".meta");
            using (var reader = new EmptyMaterialMaps.PixelReader())
            {
                var result = EmptyMaterialMaps.Analyze(() => reader.Read(texture), 99);
                Assert.That(result.Total, Is.EqualTo(pixels.Length));
                Assert.That(result.Matching, Is.EqualTo(pixels.Length - 1), string.Join("; ", reader.Read(texture).SelectMany(b => b).GroupBy(c => c).Select(g => g.Key.ToString("F6") + "=" + g.Count())));
                Assert.That(result.Qualifies(100), Is.False);
                Assert.That(result.Qualifies(99), Is.True);
            }
            Assert.That(File.ReadAllBytes(AssetDatabase.GetAssetPath(texture) + ".meta"), Is.EqualTo(metadata));
            Assert.That(texture.isReadable, Is.False);
        }

        [Test]
        public void GpuKeepsPackedAlphaAndDetectsCompressedFlatNormal()
        {
            var pixels = Enumerable.Range(0, 1024).Select(i => new Color(1, 1, 1, (i % 32) / 31f)).ToArray();
            Texture2D packed = Image("packed-alpha", pixels, 32, 32);
            Texture2D normal = Image("normal", Enumerable.Repeat(new Color(0.5f, 0.5f, 1, 1), 1024).ToArray(), 32, 32, true, true);
            using (var reader = new EmptyMaterialMaps.PixelReader())
            {
                Assert.That(EmptyMaterialMaps.Analyze(() => reader.Read(packed), 99).Qualifies(99), Is.False);
                Assert.That(EmptyMaterialMaps.Analyze(() => reader.Read(normal), 100).Qualifies(100), Is.True,
                    string.Join("; ", reader.Read(normal).SelectMany(b => b).GroupBy(c => c).Select(g => g.Key.ToString("F6") + "=" + g.Count())));
            }
        }

        [Test]
        public void RemovesAllSharedSlotsIncludingSavedUnusedOnesAndUndoRestoresWithoutSavingOrDeleting()
        {
            Texture2D texture = Image("shared", Enumerable.Repeat(Color.white, 1024).ToArray(), 32, 32);
            Material first = Material("first", texture);
            first.SetTexture("_MainTex", texture);
            first.SetTextureScale("_MainTex", new Vector2(2, 3));
            first.SetFloat("_Metallic", 0.7f);
            first.EnableKeyword("_METALLICGLOSSMAP");
            Material second = Material("second", texture);
            using (var serialized = new SerializedObject(second))
            {
                var slots = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
                int index = slots.arraySize++;
                var slot = slots.GetArrayElementAtIndex(index);
                slot.FindPropertyRelative("first").stringValue = "_OldShaderMap";
                slot.FindPropertyRelative("second.m_Texture").objectReferenceValue = texture;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            AssetDatabase.SaveAssets();
            byte[] original = File.ReadAllBytes(AssetDatabase.GetAssetPath(first));
            var materials = new[] { first, second };
            var scan = EmptyMaterialMaps.Collect(materials, 100);
            Assert.That(scan.Entries.Count, Is.EqualTo(1));
            Assert.That(scan.Entries[0].Uses.Count, Is.EqualTo(4));
            Assert.That(EmptyMaterialMaps.Remove(scan.Entries, materials), Is.EqualTo(4));
            Assert.That(first.GetTexture("_MainTex"), Is.Null);
            Assert.That(first.GetTexture("_MetallicGlossMap"), Is.Null);
            Assert.That(first.GetTextureScale("_MainTex"), Is.EqualTo(new Vector2(2, 3)));
            Assert.That(first.GetFloat("_Metallic"), Is.EqualTo(0.7f));
            Assert.That(first.IsKeywordEnabled("_METALLICGLOSSMAP"), Is.False, "Standard validates its map keyword when serialized texture references change.");
            Assert.That(EmptyMaterialMaps.Uses(second), Is.Empty);
            Assert.That(File.Exists(AssetDatabase.GetAssetPath(texture)), Is.True);
            Assert.That(File.ReadAllBytes(AssetDatabase.GetAssetPath(first)), Is.EqualTo(original));
            Undo.PerformUndo();
            Assert.That(first.GetTexture("_MainTex"), Is.EqualTo(texture));
            Assert.That(EmptyMaterialMaps.Uses(second).Count(), Is.EqualTo(2));
        }

        [Test]
        public void RejectsChangedReferencesAndReadOnlyMaterialsBeforeAnyMutation()
        {
            Texture2D texture = Image("shared", Enumerable.Repeat(Color.black, 1024).ToArray(), 32, 32);
            Material first = Material("first", texture);
            Material second = Material("second", texture);
            var scan = EmptyMaterialMaps.Collect(new[] { first }, 100);
            Assert.Throws<InvalidOperationException>(() => EmptyMaterialMaps.Remove(scan.Entries, new[] { first, second }));
            Assert.That(first.GetTexture("_MetallicGlossMap"), Is.EqualTo(texture));
            scan = EmptyMaterialMaps.Collect(new[] { first, second }, 100);
            string path = AssetDatabase.GetAssetPath(second);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                Assert.Throws<InvalidOperationException>(() => EmptyMaterialMaps.Remove(scan.Entries, new[] { first, second }));
                Assert.That(first.GetTexture("_MetallicGlossMap"), Is.EqualTo(texture));
            }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
        }

        [Test]
        public void RejectsTextureReimportAndSupportsExcludingAnIndividualTexture()
        {
            Texture2D texture = Image("shared", Enumerable.Repeat(Color.black, 1024).ToArray(), 32, 32);
            Material first = Material("first", texture);
            var scan = EmptyMaterialMaps.Collect(new[] { first }, 100);
            scan.Entries[0].Included = false;
            Assert.That(EmptyMaterialMaps.Remove(scan.Entries, new[] { first }), Is.Zero);
            scan.Entries[0].Included = true;
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));
            importer.sRGBTexture = true;
            importer.SaveAndReimport();
            Assert.Throws<InvalidOperationException>(() => EmptyMaterialMaps.Remove(scan.Entries, new[] { first }));
            Assert.That(first.GetTexture("_MetallicGlossMap"), Is.EqualTo(texture));
        }

        [Test]
        public void CancelledGpuScanRestoresRenderTargetAndLeavesMaterialsAlone()
        {
            Texture2D texture = Image("cancel", Enumerable.Repeat(Color.black, 1024).ToArray(), 32, 32);
            Material material = Material("first", texture);
            RenderTexture previous = RenderTexture.active;
            using (var reader = new EmptyMaterialMaps.PixelReader())
                Assert.Throws<OperationCanceledException>(() => EmptyMaterialMaps.Analyze(() => reader.Read(texture, _ => true), 100));
            Assert.That(RenderTexture.active, Is.EqualTo(previous));
            Assert.That(material.GetTexture("_MetallicGlossMap"), Is.EqualTo(texture));
        }

        [UnityTest]
        public IEnumerator MenuOpensAndSceneDiscoveryExcludesUnassignedProjectMaterials()
        {
            Texture2D texture = Image("project-scan", Enumerable.Repeat(Color.white, 1024).ToArray(), 32, 32);
            Material material = Material("unused-in-scene", texture);
            Assert.That(SceneMaterials.Collect(SceneMaterials.Active()), Has.No.Member(material));
            Assert.That(EditorApplication.ExecuteMenuItem("Tools/Lightbulb/Find Empty Material Maps"), Is.True);
            var window = Resources.FindObjectsOfTypeAll<EmptyMaterialMapsWindow>().Single();
            try
            {
                var scan = EmptyMaterialMaps.Collect(new[] { material }, 100);
                typeof(EmptyMaterialMapsWindow).GetField("scan", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(window, scan);
                window.Repaint();
                yield return null;
                yield return null;
                Assert.That(scan.Entries.Single().Uses.Single().Material, Is.EqualTo(material));
                Assert.That(material.GetTexture("_MetallicGlossMap"), Is.EqualTo(texture));
            }
            finally { window.Close(); }
        }

        [Test]
        public void SceneScopeIncludesInactiveRenderersTerrainAndSkyboxButExcludesOtherScenes()
        {
            Scene previous = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(previous.path))
            {
                if (!Application.isBatchMode) Assert.Ignore("Save the open scene before running the scene-scope test.");
                Assert.That(EditorSceneManager.SaveScene(previous, root + "/background.unity"), Is.True);
            }
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.That(EditorSceneManager.SaveScene(scene, root + "/active.unity"), Is.True);
            Scene other = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);
                Texture2D texture = Image("scene-shared", Enumerable.Repeat(Color.white, 1024).ToArray(), 32, 32);
                Material used = Material("used", texture);
                Material outside = Material("outside", texture);
                Material terrainMaterial = Material("terrain", texture);
                Material skyboxMaterial = Material("skybox", texture);
                Material unassigned = Material("unassigned", texture);
                var go = new GameObject("Inactive renderer");
                go.AddComponent<MeshRenderer>().sharedMaterials = new[] { used, used };
                go.SetActive(false);
                new GameObject("Terrain").AddComponent<Terrain>().materialTemplate = terrainMaterial;
                new GameObject("Camera").AddComponent<Skybox>().material = skyboxMaterial;
                SceneManager.SetActiveScene(other);
                new GameObject("Other scene").AddComponent<MeshRenderer>().sharedMaterial = outside;
                Assert.Throws<InvalidOperationException>(() => SceneMaterials.RequireActive(scene));
                SceneManager.SetActiveScene(scene);
                var materials = SceneMaterials.Collect(scene);
                Assert.That(materials.Count(m => m == used), Is.EqualTo(1));
                Assert.That(materials, Has.Member(terrainMaterial));
                Assert.That(materials, Has.Member(skyboxMaterial));
                Assert.That(materials, Has.No.Member(outside));
                Assert.That(materials, Has.No.Member(unassigned));
                var scan = EmptyMaterialMaps.Collect(materials, 100);
                Assert.That(EmptyMaterialMaps.Remove(scan.Entries, SceneMaterials.Collect(scene)), Is.EqualTo(3));
                Assert.That(used.GetTexture("_MetallicGlossMap"), Is.Null);
                Assert.That(outside.GetTexture("_MetallicGlossMap"), Is.EqualTo(texture));
                Assert.That(unassigned.GetTexture("_MetallicGlossMap"), Is.EqualTo(texture));
            }
            finally
            {
                SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
                EditorSceneManager.CloseScene(other, true);
            }
        }
    }
}
