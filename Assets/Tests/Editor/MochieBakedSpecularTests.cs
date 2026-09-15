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
    public class MochieBakedSpecularTests
    {
        private string root;
        private Scene previous, scene;
        private LightmapData[] previousMaps;
        private LightmapsMode previousMode;
        private int undoGroup;

        private static string SourcePath => MochieSpecularPatch.LightingPath(Shader.Find("Mochie/Standard"));

        [SetUp]
        public void SetUp()
        {
            if (Shader.Find("Mochie/Standard") == null) Assert.Ignore("Install Mochie Standard v2.13 for integration tests.");
            Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup();
            previous = SceneManager.GetActiveScene();
            previousMaps = LightmapSettings.lightmaps;
            previousMode = LightmapSettings.lightmapsMode;
            string name = "__LightbulbSpecularTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name);
            root = "Assets/" + name;
            if (string.IsNullOrEmpty(previous.path))
            {
                if (!Application.isBatchMode) Assert.Ignore("Save the open scene before running scene-material integration tests.");
                Assert.That(EditorSceneManager.SaveScene(previous, root + "/background.unity"), Is.True);
            }
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            LightmapSettings.lightmapsMode = LightmapsMode.CombinedDirectional;
            LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = Texture2D.whiteTexture, lightmapDir = Texture2D.whiteTexture } };
        }

        [TearDown]
        public void TearDown()
        {
            if (root == null) return;
            Undo.RevertAllDownToGroup(undoGroup);
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            LightmapSettings.lightmapsMode = previousMode;
            LightmapSettings.lightmaps = previousMaps;
            Assert.That(root, Does.StartWith("Assets/__LightbulbSpecularTests_"));
            AssetDatabase.DeleteAsset(root);
        }

        private Material MakeMaterial(string shader = "Mochie/Standard", float roughness = .5f)
        {
            var m = new Material(Shader.Find(shader));
            m.SetFloat("_RoughnessStrength", roughness);
            m.SetFloat("_BakeryMode", 3); m.EnableKeyword("BAKERY_MONOSH");
            AssetDatabase.CreateAsset(m, root + "/" + Guid.NewGuid().ToString("N") + ".mat");
            var renderer = new GameObject("Receiver").AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m; renderer.lightmapIndex = 0;
            return m;
        }

        [TestCase("\n")]
        [TestCase("\r\n")]
        public void PatchIsIdempotentAndExactlyReversible(string newline)
        {
            string original = File.ReadAllText(SourcePath).Replace("\r\n", "\n").Replace("\n", newline);
            string patched = MochieSpecularPatch.Transform(original, true);
            Assert.That(patched, Does.Contain(MochieSpecularPatch.Marker));
            Assert.That(MochieSpecularPatch.Transform(patched, true), Is.EqualTo(patched));
            Assert.That(MochieSpecularPatch.Transform(patched, false), Is.EqualTo(original));
            Assert.That(MochieSpecularPatch.Transform("// user's edit" + newline + patched, false), Is.EqualTo("// user's edit" + newline + original));
        }

        [Test]
        public void UnknownSourceOrEditedPatchIsRejected()
        {
            string original = File.ReadAllText(SourcePath);
            Assert.Throws<InvalidOperationException>(() => MochieSpecularPatch.Transform(original + "// updated", true));
            string patched = MochieSpecularPatch.Transform(original, true).Replace("lbNoH, lbRoughness", "lbNoH, 0.5");
            Assert.Throws<InvalidOperationException>(() => MochieSpecularPatch.Transform(patched, true));
            Assert.Throws<InvalidOperationException>(() => MochieSpecularPatch.Transform(patched, false));
        }

        [TestCase("Mochie/Standard")]
        [TestCase("Mochie/Standard Lite")]
        public void EnablingOnlyChangesToggleAndKeywordAndSupportsUndo(string shader)
        {
            var m = MakeMaterial(shader);
            string before = EditorJsonUtility.ToJson(m);
            var preview = MochieBakedSpecular.Collect(scene);
            Assert.That(preview.Entries.Count, Is.EqualTo(1));
            Assert.That(preview.Entries[0].Recommended, Is.True);
            Assert.That(MochieBakedSpecular.Apply(preview), Is.EqualTo(1));
            Assert.That(m.GetFloat("_BAKERY_LMSPEC"), Is.EqualTo(1));
            Assert.That(m.IsKeywordEnabled("BAKERY_LMSPEC"), Is.True);
            Assert.That(m.GetFloat("_BakeryMode"), Is.EqualTo(3));
            Assert.That(m.GetFloat("_RoughnessStrength"), Is.EqualTo(.5f));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.That(EditorJsonUtility.ToJson(m), Is.EqualTo(before));
        }

        [TestCase(0, false)]
        [TestCase(.5f, true)]
        [TestCase(1, false)]
        public void RoughnessIsARecommendationNotAReflectivityGate(float value, bool recommended)
        {
            var m = MakeMaterial(roughness: value);
            var preview = MochieBakedSpecular.Collect(scene);
            Assert.That(preview.Entries.Count, Is.EqualTo(1));
            Assert.That(preview.Entries[0].Recommended, Is.EqualTo(recommended));
            preview.Entries[0].Included = true;
            Assert.That(MochieBakedSpecular.Apply(preview), Is.EqualTo(1));
        }

        [Test]
        public void SmoothnessAndTexturedRoughnessAreNotConfusedWithScalarRoughness()
        {
            var m = MakeMaterial(roughness: .2f);
            m.SetFloat("_SmoothnessToggle", 1);
            Assert.That(MochieBakedSpecular.Recommend(m, out string reason), Is.True);
            Assert.That(reason, Does.Contain("0.8"));
            m.SetFloat("_SampleRoughness", 1);
            Assert.That(MochieBakedSpecular.Recommend(m, out _), Is.False);
            m.SetFloat("_SampleRoughness", 0); m.SetFloat("_PrimaryWorkflow", 1);
            Assert.That(MochieBakedSpecular.Recommend(m, out _), Is.False);
        }

        [Test]
        public void ZeroStrengthMissingDirectionsAndAlreadyEnabledAreSkipped()
        {
            var m = MakeMaterial();
            m.SetFloat("_BakeryLMSpecStrength", 0);
            Assert.That(MochieBakedSpecular.Collect(scene).Entries, Is.Empty);
            m.SetFloat("_BakeryLMSpecStrength", 1);
            LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = Texture2D.whiteTexture } };
            Assert.That(MochieBakedSpecular.Collect(scene).Entries, Is.Empty);
            LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = Texture2D.whiteTexture, lightmapDir = Texture2D.whiteTexture } };
            m.SetFloat("_BAKERY_LMSPEC", 1); m.EnableKeyword("BAKERY_LMSPEC");
            Assert.That(MochieBakedSpecular.Collect(scene).Entries, Is.Empty);
        }

        [Test]
        public void StaleMaterialOrReceiverAssignmentsBlockEntireBatch()
        {
            var m = MakeMaterial(); MakeMaterial();
            var preview = MochieBakedSpecular.Collect(scene);
            m.SetFloat("_RoughnessStrength", .7f);
            Assert.Throws<InvalidOperationException>(() => MochieBakedSpecular.Apply(preview));
            Assert.That(preview.Entries.All(e => e.Material.GetFloat("_BAKERY_LMSPEC") == 0), Is.True);
            preview = MochieBakedSpecular.Collect(scene);
            scene.GetRootGameObjects()[0].GetComponent<Renderer>().lightmapIndex = -1;
            Assert.Throws<InvalidOperationException>(() => MochieBakedSpecular.Apply(preview));
        }

        [Test]
        public void DuplicateAndInactiveReceiversAreDeduplicated()
        {
            var m = MakeMaterial();
            var other = new GameObject("Inactive receiver");
            var renderer = other.AddComponent<MeshRenderer>(); renderer.sharedMaterials = new[] { m, m }; renderer.lightmapIndex = 0;
            other.SetActive(false);
            Assert.That(MochieBakedSpecular.Collect(scene).Entries.Count, Is.EqualTo(1));
        }

        [Test]
        public void PatchFileRoundTripPreservesBomAndIndependentChanges()
        {
            string path = root + "/StandardLighting.cginc";
            string source = File.ReadAllText(SourcePath);
            File.WriteAllText(path, source, new System.Text.UTF8Encoding(true));
            File.Copy(Path.Combine(Path.GetDirectoryName(SourcePath), "StandardBRDF.cginc"), root + "/StandardBRDF.cginc");
            AssetDatabase.ImportAsset(path); AssetDatabase.ImportAsset(root + "/StandardBRDF.cginc");
            byte[] original = File.ReadAllBytes(path);
            MochieSpecularPatch.SetInstalled(path, true);
            Assert.That(MochieSpecularPatch.Inspect(path).Patched, Is.True);
            MochieSpecularPatch.SetInstalled(path, false);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
        }

        [TestCase("Mochie/Standard", 0)]
        [TestCase("Mochie/Standard", 1)]
        [TestCase("Mochie/Standard Lite", 0)]
        [TestCase("Mochie/Standard Lite", 1)]
        public void PatchedShaderRendersHighlightsAndOffRestoresBaseline(string shaderName, int shadingModel)
        {
            // This test temporarily patches the development project's installed shader, never the user's open world.
            if (!Application.isBatchMode) Assert.Ignore("Run this source-patching render test in the isolated development project via batch mode.");
            string path = SourcePath;
            byte[] before = File.ReadAllBytes(path);
            var color = new Texture2D(4, 4, TextureFormat.RGBAHalf, false, true);
            var direction = new Texture2D(4, 4, TextureFormat.RGBAHalf, false, true);
            var target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true);
            var oldTarget = RenderTexture.active;
            try
            {
                color.SetPixels(Enumerable.Repeat(new Color(.15f, .15f, .15f, 1), 16).ToArray()); color.Apply();
                direction.SetPixels(Enumerable.Repeat(new Color(.5f, .5f, 0, .5f), 16).ToArray()); direction.Apply();
                LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = color, lightmapDir = direction } };
                var m = MakeMaterial(shaderName);
                m.SetFloat("_BakeryMode", 0); m.DisableKeyword("BAKERY_MONOSH");
                m.SetFloat("_ShadingModel", shadingModel);
                m.SetFloat("_LightVolumesToggle", 0); m.SetFloat("_ReflectionsToggle", 0); m.DisableKeyword("_REFLECTIONS_ON");
                m.SetFloat("_BAKERY_LMSPEC", 0); m.DisableKeyword("BAKERY_LMSPEC");
                m.SetFloat("_BicubicSampling", 0); m.DisableKeyword("_BICUBIC_SAMPLING_ON");
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad); quad.layer = 30;
                var renderer = quad.GetComponent<MeshRenderer>(); renderer.sharedMaterial = m;
                GameObjectUtility.SetStaticEditorFlags(quad, StaticEditorFlags.ContributeGI);
                renderer.lightmapIndex = 0; renderer.lightmapScaleOffset = new Vector4(1, 1, 0, 0);
                renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                var camera = new GameObject("Test camera").AddComponent<Camera>(); camera.enabled = false;
                camera.transform.position = new Vector3(0, 0, -2);
                camera.orthographic = true; camera.orthographicSize = .55f;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.cullingMask = 1 << 30; camera.targetTexture = target; camera.allowHDR = true;
                camera.renderingPath = RenderingPath.Forward;
                Func<Color> render = () => {
                    camera.Render(); RenderTexture.active = target;
                    readback.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); readback.Apply();
                    return readback.GetPixel(32, 32);
                };
                Color baseline = render();
                Assert.That(baseline.r, Is.GreaterThan(.001f), "Fixture must actually receive its baked lightmap.");
                MochieSpecularPatch.SetInstalled(path, true);
                Assert.That(MochieSpecularPatch.Inspect().Patched, Is.True);
                Assert.That(render().r, Is.EqualTo(baseline.r).Within(.001f), "Off must preserve the original diffuse result.");
                var preview = MochieBakedSpecular.Collect(scene);
                Assert.That(preview.Entries.Any(e => e.Material == m), Is.True, "Patched Dominant Direction is eligible.");
                m.SetFloat("_BAKERY_LMSPEC", 1); m.EnableKeyword("BAKERY_LMSPEC");
                Color enabled = render();
                Assert.That(enabled.r, Is.GreaterThan(baseline.r + .001f), "Enabled patch must produce visible baked specular.");
                m.SetFloat("_BakeryLMSpecStrength", 0);
                Assert.That(render().r, Is.EqualTo(baseline.r).Within(.001f), "Existing strength control must work.");
                m.SetFloat("_BakeryLMSpecStrength", 1); m.SetFloat("_RoughnessStrength", 0);
                var mirror = render();
                Assert.That(float.IsFinite(mirror.r) && float.IsFinite(mirror.g) && float.IsFinite(mirror.b), Is.True, "Zero roughness must not produce NaNs/infinities.");
                direction.SetPixels(Enumerable.Repeat(new Color(.5f, .5f, .5f, .5f), 16).ToArray()); direction.Apply();
                var noDirection = render();
                Assert.That(float.IsFinite(noDirection.r), Is.True, "Missing dominant direction must be safe.");
                m.SetFloat("_BAKERY_LMSPEC", 0); m.DisableKeyword("BAKERY_LMSPEC");
                Assert.That(render().r, Is.EqualTo(noDirection.r).Within(.001f), "No dominant direction must not invent a highlight.");
                var errors = ShaderUtil.GetShaderMessages(m.shader).Where(e => e.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).ToArray();
                Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(e => e.message)));
            }
            finally
            {
                RenderTexture.active = oldTarget;
                target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(readback);
                Object.DestroyImmediate(color); Object.DestroyImmediate(direction);
                File.WriteAllBytes(path, before);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
        }
    }
}
