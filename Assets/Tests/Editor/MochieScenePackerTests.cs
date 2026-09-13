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
            string path = root + "/source.png";
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
            AssetDatabase.CreateAsset(material, root + "/material.mat");
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
            Assert.That(material.GetTexture("_MetallicMap"), Is.EqualTo(source));
            Assert.That(File.ReadAllBytes(AssetDatabase.GetAssetPath(source)), Is.EqualTo(original));
            Assert.That(MochieScenePacker.Collect(scene, true).Entries, Is.Empty);
            Undo.PerformUndo();
            Assert.That(material.GetFloat("_PrimaryWorkflow"), Is.Zero);
            Assert.That(material.GetTexture("_PackedMap"), Is.Null);
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
    }
}
