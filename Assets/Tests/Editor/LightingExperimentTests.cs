using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using static Lightbulb.WorldTools.LightingExperimentState;
using static Lightbulb.WorldTools.LightingExperimentAdapter;

namespace Lightbulb.WorldTools.Tests
{
    public sealed class LightingExperimentTests
    {
        string folder;
        LightingExperimentAdapter adapter;
        Component source;
        MeshRenderer surface;
        Material original;
        string materialFolder;
        [UnitySetUp] public IEnumerator Ready()
        {
            for (int i = 0; i < 300 && !MaterialTextureBatch.IsIdle; i++) yield return null;
            Assert.That(MaterialTextureBatch.IsIdle, Is.True, $"Editor idle: compiling={EditorApplication.isCompiling}, updating={EditorApplication.isUpdating}, playing={EditorApplication.isPlayingOrWillChangePlaymode}, building={BuildPipeline.isBuildingPlayer}");
        }

        [Test]
        public void BoundsAreDeterministicAndUseFewerVolumesForOneRoom()
        {
            var single = new[] { new Bounds(Vector3.zero, Vector3.one * 10) };
            Assert.That(LightingVolumeBounds.Fit(single, 10, .5f).Count, Is.EqualTo(1));
            var rooms = new[] { new Bounds(Vector3.left * 20, Vector3.one * 3), new Bounds(Vector3.right * 20, Vector3.one * 3), new Bounds(Vector3.up * 20, Vector3.one * 3) };
            var a = LightingVolumeBounds.Fit(rooms, 2, .2f); var b = LightingVolumeBounds.Fit(rooms.Reverse(), 2, .2f);
            Assert.That(a.Count, Is.EqualTo(2)); Assert.That(a, Is.EqualTo(b));
            foreach (Bounds room in rooms) Assert.That(a.Any(box => box.Contains(room.min) && box.Contains(room.max)), Is.True);
        }

        void SetupScene()
        {
            if (!Application.isBatchMode) Assert.Ignore("Lighting lifecycle integration runs only in an isolated batch project; it replaces the test scene.");
            if (Find("VRCLightVolumes.PointLightVolumeInstance") == null || Find("BakeryPointLight") == null) Assert.Ignore("Install native Light Volumes dev.18 and Bakery to run integration tests.");
            adapter = new LightingExperimentAdapter();
            string name = "__LightingExperimentTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name); folder = "Assets/" + name;
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, folder + "/Test.unity");
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.transform.localScale = Vector3.one * 5;
            surface = cube.GetComponent<MeshRenderer>();
            original = new Material(Shader.Find("Mochie/Standard"));
            AssetDatabase.CreateAsset(original, folder + "/Original.mat"); surface.sharedMaterial = original;
            var so = new SerializedObject(surface); so.FindProperty("m_ScaleInLightmap").floatValue = .37f; so.ApplyModifiedPropertiesWithoutUndo();
            source = new GameObject("Source Bakery").AddComponent(adapter.BakeryPoint);
            Set(source, "color", Color.cyan); Set(source, "intensity", 3f); Set(source, "shadowSpread", .2f);
            EditorSceneManager.SaveScene(scene);
        }
        LightingExperimentState Create()
        {
            var preview = LightingExperiment.Scan(true, false, 10, .5f, 1, ~0);
            var state = LightingExperiment.Create(preview, true); materialFolder = state.materialFolder; return state;
        }
        [UnityTearDown] public IEnumerator TearDown()
        {
            // Native Udon component setup posts a delayCall touching its backing behaviour.
            // Let that finish before disposing this temporary scene.
            yield return null;
            if (folder == null) yield break;
            Undo.ClearAll(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!string.IsNullOrEmpty(materialFolder)) AssetDatabase.DeleteAsset(materialFolder);
            AssetDatabase.DeleteAsset(folder); folder = null;
        }
        [Test]
        public void ModesPreserveIndependentEditsAndExcludeOffLightsFromBakeAndRendering()
        {
            SetupScene(); var state = Create();
            var lv = state.lights.Single(l => l.family == Family.PointVolume);
            var volume = state.lights.Single(l => l.family == Family.BakedVolume);
            int objects = LightingExperiment.Components(state.gameObject.scene).Count;
            Assert.That(Get<float>(lv.component, "Intensity"), Is.Zero);
            Assert.That(Get<bool>(lv.component, "Shadows"), Is.False);
            Assert.That(Get<bool>(lv.component, "RebakeShadows"), Is.False);
            Assert.That(Get<bool>(volume.component, "Bake"), Is.True);
            Assert.That(volume.component.gameObject.activeInHierarchy, Is.False);
            Assert.That(Get<bool>(lv.component, "IsActive"), Is.False);
            Assert.That(Backing(lv.component).enabled, Is.False);
            Assert.That(Backing(state.manager).enabled, Is.False);
            LightingExperiment.Switch(state, Mode.LightVolumes);
            Assert.That(Backing(lv.component).enabled, Is.True);
            Assert.That(Shader.GetGlobalFloat("_UdonPointLightVolumeCount"), Is.EqualTo(1));
            Set(lv.component, "Intensity", 123f); Set(lv.component, "Color", Color.magenta);
            Assert.That(Get<float>(source, "intensity"), Is.Zero);
            Assert.That(new SerializedObject(surface).FindProperty("m_ScaleInLightmap").floatValue, Is.Zero);
            Assert.That(surface.lightmapIndex, Is.EqualTo(-1));
            Assert.That(surface.sharedMaterial, Is.Not.SameAs(original));
            Assert.That(original.GetFloat("_LightVolumesToggle"), Is.EqualTo(1));
            LightingExperiment.Switch(state, Mode.Bakery);
            Assert.That(Get<float>(source, "intensity"), Is.EqualTo(3));
            Assert.That(new SerializedObject(surface).FindProperty("m_ScaleInLightmap").floatValue, Is.EqualTo(.37f).Within(.0001));
            Set(source, "intensity", 7f);
            LightingExperiment.Switch(state, Mode.LightVolumes);
            Assert.That(Get<float>(lv.component, "Intensity"), Is.EqualTo(123));
            Assert.That(Get<Color>(lv.component, "Color"), Is.EqualTo(Color.magenta));
            LightingExperiment.Switch(state, Mode.Bakery);
            Assert.That(Get<float>(source, "intensity"), Is.EqualTo(7));
            Assert.That(LightingExperiment.Components(state.gameObject.scene).Count, Is.EqualTo(objects));
            Assert.That(Shader.GetGlobalFloat("_UdonLightVolumeEnabled"), Is.Zero);
            Assert.That(Shader.GetGlobalFloat("_UdonPointLightVolumeCount"), Is.Zero);
        }
        [Test]
        public void ExcludedLightAndShadowSettingsSurviveRepeatedHybridSwitches()
        {
            SetupScene(); var state = Create(); var lv = state.lights.Single(l => l.family == Family.PointVolume);
            LightingExperiment.Switch(state, Mode.Hybrid);
            state.lvShadows = false; LightingExperiment.Apply(state);
            Assert.That(Get<bool>(lv.component, "RebakeShadows"), Is.False);
            state.lvShadows = true; LightingExperiment.Apply(state);
            Assert.That(Get<bool>(lv.component, "Shadows"), Is.True);
            Assert.That(Get<bool>(lv.component, "RebakeShadows"), Is.True);
            lv.included = false; LightingExperiment.Apply(state);
            Assert.That(Get<float>(lv.component, "Intensity"), Is.Zero);
            Assert.That(Get<bool>(lv.component, "IsActive"), Is.False);
            Assert.That(Get<bool>(lv.component, "BakeIntoProbes"), Is.False);
            Assert.That(Shader.GetGlobalFloat("_UdonPointLightVolumeCount"), Is.Zero);
            lv.included = true; LightingExperiment.Apply(state);
            Assert.That(Get<float>(lv.component, "Intensity"), Is.GreaterThan(0));
        }
        [UnityTest]
        public IEnumerator SaveReopenRestoresReferencesAndStoredScales()
        {
            SetupScene(); var state = Create(); LightingExperiment.Switch(state, Mode.LightVolumes);
            yield return null;
            Scene scene = state.gameObject.scene; string path = scene.path;
            EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            state = LightingExperiment.FindState(scene);
            yield return null;
            Assert.That(state, Is.Not.Null); Assert.That(state.CompareTag("EditorOnly"), Is.True);
            Assert.That(state.lights.All(l => l.component != null), Is.True);
            LightingExperiment.Switch(state, Mode.Bakery);
            Assert.That(Get<float>(state.lights.Single(l => l.family == Family.Bakery).component, "intensity"), Is.EqualTo(3));
            Assert.That(new SerializedObject(state.surfaces.Single().renderer).FindProperty("m_ScaleInLightmap").floatValue, Is.EqualTo(.37f).Within(.0001));
        }
        [Test]
        public void ReverseConversionCreatesBakeryOnceAndPreservesExistingAreaAndSpotProperties()
        {
            SetupScene();
            UnityEngine.Object.DestroyImmediate(source);
            var point = adapter.Create(true, "Original LV spot");
            Set(point, "LightType", 1); Set(point, "Angle", 40f * Mathf.Deg2Rad); Set(point, "Falloff", .25f);
            Set(point, "Intensity", 80f); Set(point, "LightSourceSize", .5f); adapter.Sync(point);
            var preview = LightingExperiment.Scan(false, false, 1, .5f, 1, ~0);
            var state = LightingExperiment.Create(preview, false); materialFolder = state.materialFolder;
            Assert.That(state.mode, Is.EqualTo(Mode.LightVolumes));
            LightingExperiment.Switch(state, Mode.Bakery);
            var bakery = state.lights.Single(l => l.family == Family.Bakery).component;
            Assert.That(Convert.ToInt32(Get(bakery, "projMode")), Is.EqualTo(4));
            Assert.That(Get<float>(bakery, "angle"), Is.EqualTo(80).Within(.001));
            Assert.That(Get<float>(bakery, "innerAngle"), Is.EqualTo(75).Within(.001));
            Assert.That(Get<float>(bakery, "intensity"), Is.EqualTo(20).Within(.001));
            LightingExperiment.Switch(state, Mode.LightVolumes);
            Assert.That(Get<float>(point, "Falloff"), Is.EqualTo(.25f));
            Assert.That(LightingExperiment.Scan(false, false, 1, .5f, 1, ~0).candidates.Count, Is.Zero);
        }
        [Test]
        public void InitiallyDisabledComponentsKeepTheirConfiguredShadowValues()
        {
            SetupScene(); ((Behaviour)source).enabled = false;
            var state = Create(); var lv = state.lights.Single(l => l.family == Family.PointVolume);
            Assert.That(lv.shadows, Is.True);
            LightingExperiment.Switch(state, Mode.LightVolumes);
            LightingExperiment.Switch(state, Mode.Bakery);
            LightingExperiment.Switch(state, Mode.LightVolumes);
            Assert.That(lv.shadows, Is.True); Assert.That(lv.enabled, Is.False);
            lv.enabled = true; LightingExperiment.Apply(state);
            Assert.That(Get<bool>(lv.component, "Shadows"), Is.True);
        }
        [UnityTest]
        public IEnumerator RectangularAreaCounterpartKeepsDimensionsAndHidesGeneratedEmitterInLVMode()
        {
            SetupScene();
            var area = adapter.Create(true, "Area source");
            area.transform.localScale = new Vector3(3, 2, 1);
            Set(area, "LightType", 2); Set(area, "Intensity", 6f); adapter.Sync(area);
            var preview = LightingExperiment.Scan(false, false, 1, .5f, 1, ~0);
            Assert.That(preview.candidates.Single(c => c.source == area).problem, Is.Null);
            var state = LightingExperiment.Create(preview, false); materialFolder = state.materialFolder;
            yield return null;
            var entry = state.lights.Single(l => l.component == area);
            Component bakery = entry.counterpart;
            Assert.That(bakery.GetType(), Is.EqualTo(adapter.BakeryMesh));
            Assert.That(bakery.transform.localScale, Is.EqualTo(new Vector3(3, 2, 1)));
            Assert.That(bakery.GetComponent<Renderer>().enabled, Is.False);
            LightingExperiment.Switch(state, Mode.Bakery);
            Assert.That(Get<float>(bakery, "intensity"), Is.EqualTo(6));
            Assert.That(bakery.GetComponent<Renderer>().enabled, Is.True);
            Assert.That(LightingExperimentConversion.Rectangle(bakery, out Vector2 size), Is.True);
            Assert.That(size, Is.EqualTo(new Vector2(3, 2)));
            LightingExperiment.Switch(state, Mode.LightVolumes);
            Assert.That(bakery.GetComponent<Renderer>().enabled, Is.False);
            Assert.That(Get<int>(area, "LightType"), Is.EqualTo(2));
        }
        [UnityTest]
        public IEnumerator FinalizeRemovesComponentsNotWorldGeometryAndUndoRestoresThem()
        {
            SetupScene(); var state = Create();
            yield return null;
            LightingExperiment.Finalize(state, false);
            Assert.That(LightingExperiment.Components(state.gameObject.scene).Any(c => c.GetType() == adapter.Point || c.GetType() == adapter.Volume || c.GetType() == adapter.Manager), Is.False);
            Assert.That(surface, Is.Not.Null); Assert.That(source, Is.Not.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(folder + "/Original.mat"), Is.Not.Null);
            Undo.PerformUndo();
            Assert.That(state.finished, Is.False);
            Assert.That(state.lights.All(l => l.component != null), Is.True);
        }
        [Test]
        public void StalePreviewAndAdditiveScenesAreRejectedWithoutMutation()
        {
            SetupScene(); var preview = LightingExperiment.Scan(true, false, 1, .5f, 1, ~0);
            Set(source, "intensity", 4f);
            Assert.Throws<InvalidOperationException>(() => LightingExperiment.Create(preview, true));
            Assert.That(LightingExperiment.FindState(SceneManager.GetActiveScene()), Is.Null);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.Throws<InvalidOperationException>(() => LightingExperiment.Scan(true, false, 1, .5f, 1, ~0));
        }
        [UnityTest]
        public IEnumerator NativeShadowBakeProducesOnlyIncludedLightOutput()
        {
            SetupScene();
            // Test native shadow inclusion independently of third-party surface shader passes.
            original.shader = Shader.Find("Unlit/Color");
            var other = new GameObject("Excluded source").AddComponent(adapter.BakeryPoint);
            Set(other, "intensity", 2f);
            var state = Create(); LightingExperiment.Switch(state, Mode.LightVolumes);
            var points = state.lights.Where(l => l.family == Family.PointVolume).ToArray();
            points[1].included = false;
            Set(state.manager, "ShadowTexturesWidth", 32); Set(state.manager, "ShadowTexturesHeight", 32);
            foreach (var point in points) { Set(point.component, "ShadowBakeResolution", 32); Set(point.component, "Blur", 0f); }
            yield return null;
            LightingExperiment.BakeShadows(state);
            Assert.That(Get(points[0].component, "ShadowMap") as UnityEngine.Object, Is.Not.Null);
            Assert.That((Get(points[1].component, "ShadowMap") as UnityEngine.Object) == null, Is.True);
            Assert.That(Shader.GetGlobalFloat("_UdonPointLightVolumeCount"), Is.EqualTo(1));
        }
    }
}
