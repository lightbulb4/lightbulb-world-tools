using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using static Lightbulb.WorldTools.MochieLightVolumes;

namespace Lightbulb.WorldTools.Tests
{
    public sealed class MochieLightVolumesTests
    {
        private string folder;
        private Material standard, lite, unrelated, unused;
        private Scene scene;

        [UnitySetUp] public IEnumerator Setup()
        {
            if (!Application.isBatchMode) Assert.Ignore("Scene lifecycle tests run only in the isolated batch project.");
            folder = "Assets/__MochieLightVolumesTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Lightmapping.giWorkflowMode = Lightmapping.GIWorkflowMode.OnDemand;
            Lightmapping.Cancel();
            standard = MakeMaterial("Standard", MochieShader(false));
            lite = MakeMaterial("Lite", MochieShader(true));
            unused = MakeMaterial("Unused", standard.shader);
            unrelated = MakeMaterial("Other", Shader.Find("Unlit/Color"));
            new GameObject("First").AddComponent<MeshRenderer>().sharedMaterials = new[] { standard, standard, unrelated };
            var inactive = new GameObject("Inactive skinned");
            inactive.AddComponent<SkinnedMeshRenderer>().sharedMaterials = new[] { standard, lite };
            inactive.SetActive(false);
            EditorSceneManager.SaveScene(scene, folder + "/Test.unity");
            yield return null;
            for (int i = 0; i < 600 && !SceneMaterials.IsLightingIdle; i++) yield return null;
            Assert.That(SceneMaterials.IsLightingIdle, Is.True, $"Fixture idle: compiling={EditorApplication.isCompiling}, updating={EditorApplication.isUpdating}, playing={EditorApplication.isPlayingOrWillChangePlaymode}, building={BuildPipeline.isBuildingPlayer}, baking={Lightmapping.isRunning}");
        }

        private Shader MochieShader(bool isLite)
        {
            string name = "Standard" + (isLite ? " Lite" : "");
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Mochie/Standard Shader/" + name + ".shader");
            if (shader != null) return shader;
            // A minimal property contract keeps the package tests independent of third-party assets.
            string path = folder + "/" + name + ".shader";
            string properties = string.Join(" ", Settings.Select(s => s.Property + " (\"" + s.Label + "\", Float) = 0"));
            System.IO.File.WriteAllText(path, "Shader \"Mochie/" + name + "\" { Properties { " + properties + " } SubShader { Pass {} } }");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Shader>(path);
        }

        private Material MakeMaterial(string name, Shader shader)
        {
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        [TearDown] public void TearDown()
        {
            if (folder == null) return;
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(folder);
            folder = null;
        }

        [Test] public void ScanIncludesInactiveSkinnedAndDeduplicatesOnlyAssignedMochieMaterials()
        {
            Assert.That(Collect(scene), Is.EquivalentTo(new[] { standard, lite }));
            foreach (Material material in Collect(scene))
                foreach (Setting setting in Settings) Assert.That(material.HasProperty(setting.Property), Is.True, setting.Property);
        }

        [Test] public void SetsEverySupportedPropertyAndPreservesOtherMaterialsAndSettings()
        {
            standard.renderQueue = 2451;
            var edits = Settings.Select((s, i) => new Edit(i, Operation.Set, s.Toggle ? 1 : .75f)).ToArray();
            float unusedBefore = unused.GetFloat(Settings[1].Property);
            Assert.That(Apply(scene, edits), Is.EqualTo(2));
            foreach (Material material in Collect(scene))
                for (int i = 0; i < Settings.Length; i++)
                    Assert.That(material.GetFloat(Settings[i].Property), Is.EqualTo(Settings[i].Toggle ? 1 : .75f));
            Assert.That(standard.renderQueue, Is.EqualTo(2451));
            Assert.That(unused.GetFloat(Settings[1].Property), Is.EqualTo(unusedBefore));
            Assert.That(unrelated.shader.name, Is.EqualTo("Unlit/Color"));
            Assert.That(Apply(scene, edits), Is.Zero, "Already matching materials should not be rewritten.");
        }

        [Test] public void RelativeSpecularStepsUseEachOriginalAndLeaveOtherSettingsAlone()
        {
            standard.SetFloat(Settings[SpecularStrength].Property, .25f);
            lite.SetFloat(Settings[SpecularStrength].Property, 5.5f);
            standard.SetFloat(Settings[4].Property, 0);
            lite.SetFloat(Settings[4].Property, 1);
            Apply(scene, new[] { new Edit(SpecularStrength, Operation.Add, 1) });
            Assert.That(standard.GetFloat(Settings[SpecularStrength].Property), Is.EqualTo(1.25f));
            Assert.That(lite.GetFloat(Settings[SpecularStrength].Property), Is.EqualTo(6.5f));
            Apply(scene, new[] { new Edit(SpecularStrength, Operation.Add, -1) });
            Assert.That(standard.GetFloat(Settings[SpecularStrength].Property), Is.EqualTo(.25f));
            Assert.That(lite.GetFloat(Settings[SpecularStrength].Property), Is.EqualTo(5.5f));
            Assert.That(standard.GetFloat(Settings[4].Property), Is.Zero);
            Assert.That(lite.GetFloat(Settings[4].Property), Is.EqualTo(1));
        }

        [Test] public void KeepOffRangeLimitsAndUnrestrictedBiasMatchMochieFields()
        {
            standard.SetFloat(Settings[0].Property, 1);
            Apply(scene, new[] { new Edit(0, Operation.Keep, 0), new Edit(2, Operation.Set, 0),
                new Edit(1, Operation.Set, 16), new Edit(6, Operation.Set, -2) });
            Assert.That(standard.GetFloat(Settings[0].Property), Is.EqualTo(1));
            Assert.That(standard.GetFloat(Settings[2].Property), Is.Zero);
            Assert.That(standard.GetFloat(Settings[1].Property), Is.EqualTo(1));
            Assert.That(standard.GetFloat(Settings[6].Property), Is.EqualTo(-2));
            Apply(scene, new[] { new Edit(1, Operation.Add, -2) });
            Assert.That(standard.GetFloat(Settings[1].Property), Is.Zero);
        }

        [Test] public void PreflightRejectsReadonlyMaterialBeforeChangingAnyMaterial()
        {
            float before = lite.GetFloat(Settings[6].Property);
            standard.hideFlags |= HideFlags.NotEditable;
            try
            {
                Assert.Throws<InvalidOperationException>(() => Apply(scene, new[] { new Edit(6, Operation.Set, 42) }));
                Assert.That(lite.GetFloat(Settings[6].Property), Is.EqualTo(before));
            }
            finally { standard.hideFlags &= ~HideFlags.NotEditable; }
        }

        [Test] public void InvalidInputAndOverflowCannotPartiallyApply()
        {
            float before = standard.GetFloat(Settings[6].Property);
            Assert.Throws<ArgumentException>(() => Apply(scene, new[] { new Edit(6, Operation.Set, 42), new Edit(1, Operation.Set, float.NaN) }));
            Assert.Throws<ArgumentException>(() => Apply(scene, new[] { new Edit(0, Operation.Add, 1) }));
            Assert.Throws<ArgumentException>(() => Apply(scene, new[] { new Edit(6, Operation.Set, 1), new Edit(6, Operation.Add, 2) }));
            standard.SetFloat(Settings[SpecularStrength].Property, float.MaxValue);
            Assert.Throws<InvalidOperationException>(() => Apply(scene, new[] { new Edit(6, Operation.Set, 42), new Edit(SpecularStrength, Operation.Add, float.MaxValue) }));
            Assert.That(standard.GetFloat(Settings[6].Property), Is.EqualTo(before));
        }

        [UnityTest] public IEnumerator MissingShaderPropertyBlocksTheWholeBatch()
        {
            string path = folder + "/OldMochie.shader";
            System.IO.File.WriteAllText(path, "Shader \"Mochie/Standard\" { Properties { _LightVolumesToggle (\"Volumes\", Float) = 1 } SubShader { Pass {} } }");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            standard.shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            for (int i = 0; i < 600 && !SceneMaterials.IsLightingIdle; i++) yield return null;
            float before = lite.GetFloat(Settings[6].Property);
            Assert.Throws<InvalidOperationException>(() => Apply(scene, new[] { new Edit(6, Operation.Set, 42) }));
            Assert.That(lite.GetFloat(Settings[6].Property), Is.EqualTo(before));
        }

        [Test] public void NormalUnityUndoRestoresDifferentOriginalValues()
        {
            standard.SetFloat(Settings[6].Property, .1f);
            lite.SetFloat(Settings[6].Property, .2f);
            Apply(scene, new[] { new Edit(6, Operation.Set, .5f) });
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Assert.That(standard.GetFloat(Settings[6].Property), Is.EqualTo(.1f));
            Assert.That(lite.GetFloat(Settings[6].Property), Is.EqualTo(.2f));
        }

        [Test] public void CountsDeduplicateMaterialsAndAccountForToggleAndNumericBuckets()
        {
            standard.SetFloat(Settings[0].Property, 1);
            lite.SetFloat(Settings[0].Property, 0);
            Assert.That(Counts(new[] { standard, standard, lite }, 0), Is.EqualTo("On: 1    Off: 1"));
            standard.SetFloat(Settings[6].Property, 0);
            lite.SetFloat(Settings[6].Property, 1);
            unused.SetFloat(Settings[6].Property, 2.5f);
            Assert.That(Counts(new[] { standard, lite, unused }, 6), Is.EqualTo("0: 1    1: 1    >1: 1"));
            lite.SetFloat(Settings[6].Property, .5f);
            unused.SetFloat(Settings[6].Property, -1);
            Assert.That(Counts(new[] { standard, lite, unused }, 6), Is.EqualTo("0: 1    1: 0    >1: 0    Other: 2"));
            Assert.That(Counts(new[] { unrelated }, 6), Does.Contain("Missing property: 1"));
        }
    }
}
