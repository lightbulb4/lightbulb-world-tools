using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Lightbulb.WorldTools.Tests
{
    public sealed class DuplicateLightmapUsageTests
    {
        string folder, scenePath;
        Material firstMaterial, secondMaterial;
        Texture2D atlas;
        MeshRenderer first, second;

        [UnitySetUp] public IEnumerator Setup()
        {
            if (!Application.isBatchMode) Assert.Ignore("Scene lifecycle tests run in the isolated batch project.");
            for (int n = 0; n < 300 && !MaterialTextureBatch.IsIdle; n++) yield return null;
            folder = "Assets/__DuplicateLightmapTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scenePath = folder + "/Test.unity";
            firstMaterial = new Material(Shader.Find("Standard"));
            secondMaterial = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(firstMaterial, folder + "/First.mat");
            AssetDatabase.CreateAsset(secondMaterial, folder + "/Second.mat");
            atlas = new Texture2D(64, 32);
            first = Make("First", firstMaterial, secondMaterial);
            second = Make("Second", firstMaterial, secondMaterial);
            EditorSceneManager.SaveScene(scene, scenePath);
            yield return null;
            for (int n = 0; n < 600 && !MaterialTextureBatch.IsIdle; n++) yield return null;
            LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = atlas } };
            Baked(first, new Vector4(.5f, .5f, 0, 0));
            Baked(second, new Vector4(.5f, .5f, .25f, 0));
        }

        MeshRenderer Make(string name, params Material[] materials)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterials = materials;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic);
            renderer.receiveGI = ReceiveGI.Lightmaps;
            RendererLightingSettings.SetScale(renderer, 1);
            return renderer;
        }
        static void Baked(MeshRenderer renderer, Vector4 rect) { renderer.lightmapIndex = 0; renderer.lightmapScaleOffset = rect; }
        DuplicateLightmapUsage.ScanResult Scan(bool inactive = true) => DuplicateLightmapUsage.Scan(SceneManager.GetActiveScene(), inactive);

        [TearDown] public void Cleanup()
        {
            if (folder == null) return;
            Undo.ClearAll();
            LightmapSettings.lightmaps = Array.Empty<LightmapData>();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (atlas != null) UnityEngine.Object.DestroyImmediate(atlas);
            AssetDatabase.DeleteAsset(folder); folder = null;
        }

        [Test] public void GroupsUseMeshIdentityAndEveryOrderedMaterialSlotAndOnlyTheActiveScene()
        {
            Make("Reversed slots", secondMaterial, firstMaterial);
            Make("Other second slot", firstMaterial, firstMaterial);
            var otherMesh = Make("Other mesh", firstMaterial, secondMaterial);
            otherMesh.GetComponent<MeshFilter>().sharedMesh = UnityEngine.Object.Instantiate(first.GetComponent<MeshFilter>().sharedMesh);
            var missing = new GameObject("Missing mesh").AddComponent<MeshRenderer>();
            var active = SceneManager.GetActiveScene();
            var additive = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Make("Other scene", firstMaterial, secondMaterial);
            SceneManager.SetActiveScene(active);
            var scan = Scan();
            Assert.That(scan.Groups.Length, Is.EqualTo(1));
            Assert.That(scan.Groups[0].Renderers, Is.EquivalentTo(new[] { first, second }));
            Assert.That(scan.MissingMeshes, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => DuplicateLightmapUsage.Apply(scan, scan.Groups[0], 0, false));
            EditorSceneManager.CloseScene(additive, true);
            UnityEngine.Object.DestroyImmediate(otherMesh.GetComponent<MeshFilter>().sharedMesh);
            UnityEngine.Object.DestroyImmediate(missing.gameObject);
        }

        [Test] public void InactiveCountsAndOverlappingFootprintsDoNotDoubleCountAtlasRectangles()
        {
            second.gameObject.SetActive(false);
            second.enabled = false;
            var group = Scan().Groups.Single();
            Assert.That(group.BakedCount, Is.EqualTo(2));
            Assert.That(group.ConfiguredCount, Is.EqualTo(2));
            Assert.That(group.InactiveCount, Is.EqualTo(1));
            Assert.That(group.DisabledCount, Is.EqualTo(1));
            Assert.That(group.Texels, Is.EqualTo(.375 * 64 * 32).Within(.001));
            Assert.That(group.OverlappingAllocations, Is.True);
            Assert.That(Scan(false).Groups, Is.Empty);
            Baked(second, first.lightmapScaleOffset);
            Assert.That(Scan().Groups.Single().Texels, Is.EqualTo(.25 * 64 * 32).Within(.001));
            var singleton = Make("Shared allocation outside category", firstMaterial);
            Baked(singleton, first.lightmapScaleOffset);
            Assert.That(Scan().Groups.Single().OverlappingAllocations, Is.True);
        }

        [Test] public void InvalidOrMissingBakeBindingsRemainConfiguredWithoutInventedFootprints()
        {
            first.lightmapIndex = 0xfffe; second.lightmapIndex = 20;
            var group = Scan().Groups.Single();
            Assert.That(group.BakedCount, Is.Zero);
            Assert.That(group.ConfiguredCount, Is.EqualTo(2));
            Assert.That(group.Texels, Is.Zero);
            LightmapSettings.lightmaps = new[] { new LightmapData() };
            first.lightmapIndex = 0;
            Assert.That(Scan().Groups.Single().BakedCount, Is.Zero);
        }

        [Test] public void CategoryScalePreservesIndividualUndoValuesAndLeavesProbeReceiversAndMaterialsAlone()
        {
            RendererLightingSettings.SetScale(first, .25f); RendererLightingSettings.SetScale(second, 2);
            var probe = Make("Already probes", firstMaterial, secondMaterial);
            probe.receiveGI = ReceiveGI.LightProbes;
            var scan = Scan();
            DuplicateLightmapUsage.Apply(scan, scan.Groups.Single(), 0, false);
            Assert.That(RendererLightingSettings.Scale(first), Is.Zero);
            Assert.That(RendererLightingSettings.Scale(second), Is.Zero);
            Assert.That(RendererLightingSettings.Scale(probe), Is.EqualTo(1));
            Assert.That(first.receiveGI, Is.EqualTo(ReceiveGI.Lightmaps));
            Assert.That(first.sharedMaterials, Is.EqualTo(new[] { firstMaterial, secondMaterial }));
            Assert.That(first.lightmapIndex, Is.Zero, "The existing bake must not be cleared by a settings edit.");
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.That(RendererLightingSettings.Scale(first), Is.EqualTo(.25f));
            Assert.That(RendererLightingSettings.Scale(second), Is.EqualTo(2));
            Undo.PerformRedo(); Assert.That(RendererLightingSettings.Scale(second), Is.Zero);
        }

        [Test] public void ProbeActionIsSeparateFromScaleAndPreservesStaticFlagsWithUndo()
        {
            RendererLightingSettings.SetScale(first, .37f);
            first.lightProbeUsage = LightProbeUsage.Off;
            var flags = GameObjectUtility.GetStaticEditorFlags(first.gameObject);
            var scan = Scan(); DuplicateLightmapUsage.Apply(scan, scan.Groups.Single(), null, true);
            Assert.That(first.receiveGI, Is.EqualTo(ReceiveGI.LightProbes));
            Assert.That(first.lightProbeUsage, Is.EqualTo(LightProbeUsage.BlendProbes));
            Assert.That(RendererLightingSettings.Scale(first), Is.EqualTo(.37f));
            Assert.That(GameObjectUtility.GetStaticEditorFlags(first.gameObject), Is.EqualTo(flags));
            Assert.That(first.sharedMaterials, Is.EqualTo(new[] { firstMaterial, secondMaterial }));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.That(first.receiveGI, Is.EqualTo(ReceiveGI.Lightmaps));
            Assert.That(first.lightProbeUsage, Is.EqualTo(LightProbeUsage.Off));
        }

        [Test] public void StaleMembershipSettingsAndInvalidNumbersFailBeforeAnyMutation()
        {
            var scan = Scan(); Make("New matching object", firstMaterial, secondMaterial);
            Assert.Throws<InvalidOperationException>(() => DuplicateLightmapUsage.Apply(scan, scan.Groups.Single(), 0, false));
            Assert.That(RendererLightingSettings.Scale(first), Is.EqualTo(1));
            scan = Scan(); RendererLightingSettings.SetScale(second, 3);
            Assert.Throws<InvalidOperationException>(() => DuplicateLightmapUsage.Apply(scan, scan.Groups.Single(), 0, false));
            scan = Scan();
            Assert.Throws<ArgumentOutOfRangeException>(() => DuplicateLightmapUsage.Apply(scan, scan.Groups.Single(), float.NaN, false));
            Assert.Throws<ArgumentOutOfRangeException>(() => DuplicateLightmapUsage.Apply(scan, scan.Groups.Single(), -1, false));
            Assert.That(RendererLightingSettings.Scale(first), Is.EqualTo(1));
            Assert.That(RendererLightingSettings.Scale(second), Is.EqualTo(3));
        }

        [UnityTest] public IEnumerator PrefabScaleOverridesSurviveSceneSaveAndReload()
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(first.gameObject, folder + "/Prop.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = "Prefab instance";
            yield return null;
            for (int n = 0; n < 600 && !MaterialTextureBatch.IsIdle; n++) yield return null;
            var scan = Scan(); DuplicateLightmapUsage.Apply(scan, scan.Groups.Single(), .5f, false);
            Assert.That(PrefabUtility.GetPropertyModifications(instance).Any(p => p.propertyPath == "m_ScaleInLightmap"), Is.True);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.OpenScene(scenePath);
            Assert.That(RendererLightingSettings.Scale(GameObject.Find("Prefab instance").GetComponent<MeshRenderer>()), Is.EqualTo(.5f));
        }

        [UnityTest] public IEnumerator MenuWindowScansAndDrawsExpandedResultsAndInvalidatesAfterUndo()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Window rendering needs a graphics device; run this smoke test without -nographics.");
            Assert.That(EditorApplication.ExecuteMenuItem("Tools/Lightbulb/Duplicate Lightmap Usage"), Is.True);
            var window = EditorWindow.GetWindow<DuplicateLightmapUsageWindow>();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var scanMethod = typeof(DuplicateLightmapUsageWindow).GetMethod("Scan", flags);
            var scanField = typeof(DuplicateLightmapUsageWindow).GetField("scan", flags);
            try
            {
                scanMethod.Invoke(window, null);
                var scanned = (DuplicateLightmapUsage.ScanResult)scanField.GetValue(window);
                Assert.That(scanned.Groups.Length, Is.EqualTo(1));
                scanned.Groups[0].Expanded = true;
                window.SendEvent(new Event { type = EventType.Layout });
                window.SendEvent(new Event { type = EventType.Repaint });
                yield return null;
                DuplicateLightmapUsage.Apply(scanned, scanned.Groups.Single(), 0, false);
                Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
                Assert.That(scanField.GetValue(window), Is.Null);
            }
            finally { window.Close(); }
        }
    }
}
