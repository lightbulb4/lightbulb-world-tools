using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Lightbulb.WorldTools.Tests
{
    public sealed class LightmapVolumeSwapTests
    {
        string folder, scenePath;
        Material material, other;
        MeshRenderer first, second;
        [UnitySetUp] public IEnumerator Setup()
        {
            if (!Application.isBatchMode) Assert.Ignore("Scene lifecycle tests run only in the isolated batch project.");
            for (int n = 0; n < 300 && !MaterialTextureBatch.IsIdle; n++) yield return null;
            folder = "Assets/__LightmapVolumeSwapTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scenePath = folder + "/Test.unity";
            var shader = Shader.Find("Mochie/Standard");
            if (shader == null)
            {
                string path = folder + "/MochieContract.shader";
                System.IO.File.WriteAllText(path, "Shader \"Mochie/Standard\" { Properties { _LightVolumesToggle (\"Volumes\", Float) = 0 _LightVolumeSpecularity (\"Specularity\", Float) = 0 } SubShader { Pass {} } }");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            }
            material = new Material(shader);
            material.SetFloat(LightmapVolumeSwap.Volumes, 0);
            material.SetFloat(LightmapVolumeSwap.Specularity, 1);
            AssetDatabase.CreateAsset(material, folder + "/Material.mat");
            other = new Material(Shader.Find("Unlit/Color"));
            AssetDatabase.CreateAsset(other, folder + "/Other.mat");
            first = Make("First", material, true, .37f);
            second = Make("Second", material, true, 2.5f);
            second.gameObject.SetActive(false);
            EditorSceneManager.SaveScene(scene, scenePath);
            yield return null;
            for (int n = 0; n < 600 && !MaterialTextureBatch.IsIdle; n++) yield return null;
            Assert.That(MaterialTextureBatch.IsIdle, Is.True, $"Idle after fixture import: compiling={EditorApplication.isCompiling}, updating={EditorApplication.isUpdating}, playing={EditorApplication.isPlayingOrWillChangePlaymode}, building={BuildPipeline.isBuildingPlayer}");
        }
        MeshRenderer Make(string name, Material mat, bool gi, float scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = mat;
            GameObjectUtility.SetStaticEditorFlags(go, gi ? StaticEditorFlags.ContributeGI : 0);
            SetScale(renderer, scale); return renderer;
        }
        static void SetScale(MeshRenderer renderer, float scale)
        {
            using (var so = new SerializedObject(renderer)) { so.FindProperty("m_ScaleInLightmap").floatValue = scale; so.ApplyModifiedPropertiesWithoutUndo(); }
        }
        [TearDown] public void Cleanup()
        {
            if (folder == null) return;
            Undo.ClearAll(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(folder); folder = null;
        }
        [Test] public void ScanIncludesInactiveContributorsDeduplicatesMaterialsAndReportsSharedAndMixedUses()
        {
            Make("Non GI", material, false, 1);
            Make("Different shader", other, true, 1);
            first.sharedMaterials = new[] { material, other };
            var preview = LightmapVolumeSwap.Scan(SceneManager.GetActiveScene());
            Assert.That(preview.renderers.Length, Is.EqualTo(2));
            Assert.That(preview.materials.Length, Is.EqualTo(1));
            Assert.That(preview.otherMaterialUsers, Is.EqualTo(1));
            Assert.That(preview.mixedRenderers, Is.EqualTo(1));
        }
        [Test] public void ReverseRestoresIndividualOriginalsAndSupportsUndoRedo()
        {
            var state = LightmapVolumeSwap.Apply(LightmapVolumeSwap.Scan(SceneManager.GetActiveScene()), true, true, true);
            Assert.That(LightmapVolumeSwap.Scale(first), Is.Zero);
            Assert.That(LightmapVolumeSwap.Scale(second), Is.Zero);
            Assert.That(material.GetFloat(LightmapVolumeSwap.Volumes), Is.EqualTo(1));
            Assert.That(GameObjectUtility.GetStaticEditorFlags(first.gameObject) & StaticEditorFlags.ContributeGI, Is.Not.Zero);
            LightmapVolumeSwap.Restore(state);
            Assert.That(LightmapVolumeSwap.Scale(first), Is.EqualTo(.37f));
            Assert.That(LightmapVolumeSwap.Scale(second), Is.EqualTo(2.5f));
            Assert.That(material.GetFloat(LightmapVolumeSwap.Volumes), Is.Zero);
            Assert.That(material.GetFloat(LightmapVolumeSwap.Specularity), Is.EqualTo(1));
            Undo.PerformUndo();
            Assert.That(state.applied, Is.True);
            Assert.That(LightmapVolumeSwap.Scale(first), Is.Zero);
            Undo.PerformRedo();
            Assert.That(state.applied, Is.False);
            Assert.That(LightmapVolumeSwap.Scale(first), Is.EqualTo(.37f));
        }
        [Test] public void OptionsOnlyChangeAndRestoreSelectedProperties()
        {
            var state = LightmapVolumeSwap.Apply(LightmapVolumeSwap.Scan(SceneManager.GetActiveScene()), false, false, true);
            Assert.That(LightmapVolumeSwap.Scale(first), Is.EqualTo(.37f));
            Assert.That(material.GetFloat(LightmapVolumeSwap.Volumes), Is.Zero);
            material.SetFloat(LightmapVolumeSwap.Volumes, 1);
            LightmapVolumeSwap.Restore(state);
            Assert.That(material.GetFloat(LightmapVolumeSwap.Volumes), Is.EqualTo(1));
        }
        [Test] public void StalePlanAndMissingRestoreTargetFailBeforeAnyMutation()
        {
            var preview = LightmapVolumeSwap.Scan(SceneManager.GetActiveScene());
            SetScale(first, 3);
            Assert.Throws<InvalidOperationException>(() => LightmapVolumeSwap.Apply(preview, true, true, true));
            Assert.That(LightmapVolumeSwap.State(SceneManager.GetActiveScene()), Is.Null);
            var state = LightmapVolumeSwap.Apply(LightmapVolumeSwap.Scan(SceneManager.GetActiveScene()), true, true, true);
            UnityEngine.Object.DestroyImmediate(second.gameObject);
            Assert.Throws<InvalidOperationException>(() => LightmapVolumeSwap.Restore(state));
            Assert.That(LightmapVolumeSwap.Scale(first), Is.Zero);
            Assert.That(state.applied, Is.True);
        }
        [UnityTest] public IEnumerator SavedSceneRetainsRestoreRecordAcrossReload()
        {
            LightmapVolumeSwap.Apply(LightmapVolumeSwap.Scan(SceneManager.GetActiveScene()), true, true, true);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene()); AssetDatabase.SaveAssets();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scene = EditorSceneManager.OpenScene(scenePath);
            yield return null;
            for (int n = 0; n < 600 && !MaterialTextureBatch.IsIdle; n++) yield return null;
            var state = LightmapVolumeSwap.State(scene);
            Assert.That(state.applied, Is.True);
            Assert.That(state.CompareTag("EditorOnly"), Is.True);
            LightmapVolumeSwap.Restore(state);
            Assert.That(state.surfaces[0].renderer, Is.Not.Null);
            foreach (var s in state.surfaces) Assert.That(LightmapVolumeSwap.Scale(s.renderer), Is.EqualTo(s.scale));
            Assert.That(state.materials[0].material.GetFloat(LightmapVolumeSwap.Volumes), Is.Zero);
        }
        [Test] public void ApplyUndoRemovesRecordAndRestoresSettings()
        {
            LightmapVolumeSwap.Apply(LightmapVolumeSwap.Scan(SceneManager.GetActiveScene()), true, true, true);
            Undo.PerformUndo();
            Assert.That(LightmapVolumeSwap.State(SceneManager.GetActiveScene()), Is.Null);
            Assert.That(LightmapVolumeSwap.Scale(first), Is.EqualTo(.37f));
            Assert.That(material.GetFloat(LightmapVolumeSwap.Volumes), Is.Zero);
        }
    }
}
