using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.Timeline;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools.Tests
{
    public sealed class SceneCleanupTests
    {
        private Scene scene;
        private string folder;
        private readonly List<Object> temporary = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            folder = "Assets/__LightbulbSceneCleanup_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            EditorSceneManager.SaveScene(scene, folder + "/scene.unity");
        }

        [UnitySetUp]
        public IEnumerator Ready()
        {
            for (int i = 0; i < 300 && !MaterialTextureBatch.IsIdle; i++) yield return null;
            Assert.That(MaterialTextureBatch.IsIdle, Is.True, "Wait for SDK initialization before running scene tests.");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<SceneTextureCrunchWindow>()) window.Close();
            foreach (var window in Resources.FindObjectsOfTypeAll<DisabledObjectCleanupWindow>()) window.Close();
            Undo.ClearAll();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            foreach (Object value in temporary) if (value != null && !EditorUtility.IsPersistent(value)) Object.DestroyImmediate(value);
            temporary.Clear();
            AssetDatabase.DeleteAsset(folder);
        }

        private GameObject Go(string name, bool active = true, GameObject parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent.transform);
            go.SetActive(active);
            return go;
        }

        private Texture2D Texture(string name)
        {
            var image = new Texture2D(32, 32);
            File.WriteAllBytes(folder + "/" + name + ".png", image.EncodeToPNG());
            Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(folder + "/" + name + ".png", ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + name + ".png");
        }

        private Material Mat(Texture texture)
        {
            var material = new Material(Shader.Find("Standard"));
            material.mainTexture = texture;
            temporary.Add(material);
            return material;
        }

        private DisabledObjectCleanup.Entry Entry(DisabledObjectCleanup.Scan scan, GameObject go) => scan.Entries.Single(e => e.Object == go);

        [Test]
        public void DisabledRootsIgnoreInternalReferencesAndRestoreOriginalTags()
        {
            GameObject root = Go("Disabled", false);
            root.tag = "Respawn";
            GameObject child = Go("Child", true, root);
            root.AddComponent<SceneScanFixture>().References = new Object[] { child.transform };
            var scan = DisabledObjectCleanup.Collect();
            Assert.That(scan.Uncertainties, Is.Empty);
            Assert.That(scan.Entries.Count, Is.EqualTo(1));
            Assert.That(Entry(scan, root).Candidate, Is.True);
            var changes = DisabledObjectCleanup.Apply(scan);
            Assert.That(root.tag, Is.EqualTo("EditorOnly"));
            Assert.That(child.tag, Is.EqualTo("Untagged"));
            Assert.That(root.activeSelf, Is.False);
            Assert.That(DisabledObjectCleanup.Restore(changes), Is.EqualTo(1));
            Assert.That(root.tag, Is.EqualTo("Respawn"));
        }

        [Test]
        public void ExternalComponentReferencesAndEventsProtectWholeBranch()
        {
            var branch = Go("Branch", false);
            var child = Go("Child", false, branch);
            var holder = Go("Holder").AddComponent<SceneScanFixture>();
            holder.References = new Object[] { child.transform };
            UnityEventTools.AddBoolPersistentListener(holder.Event, child.SetActive, true);
            var scan = DisabledObjectCleanup.Collect();
            Assert.That(Entry(scan, branch).Candidate, Is.False);
            Assert.That(Entry(scan, child).Reasons.Any(r => r.Contains("References")), Is.True);
            Assert.That(Entry(scan, child).Reasons.Any(r => r.Contains("Event")), Is.True);
        }

        [Test]
        public void AnimatorOverridePathsAndLegacyAnimationProtectDisabledDescendants()
        {
            var root = Go("Animated");
            var child = Go("Child", false, root);
            var clip = new AnimationClip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Child", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            AssetDatabase.CreateAsset(clip, folder + "/clip.anim");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/controller.controller");
            controller.AddMotion(clip);
            var overrides = new AnimatorOverrideController(controller);
            temporary.Add(overrides);
            root.AddComponent<Animator>().runtimeAnimatorController = overrides;
            Assert.That(Entry(DisabledObjectCleanup.Collect(), child).Reasons.Any(r => r.Contains("Animation clip")), Is.True);
            Object.DestroyImmediate(root.GetComponent<Animator>());
            clip.legacy = true;
            root.AddComponent<Animation>().AddClip(clip, "Test");
            Assert.That(Entry(DisabledObjectCleanup.Collect(), child).Candidate, Is.False);
        }

        [Test]
        public void TimelineProtectsBoundHierarchyAndExposedReferences()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            temporary.Add(timeline);
            var track = timeline.CreateTrack<AnimationTrack>(null, "Animation");
            var director = Go("Director").AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            var root = Go("BoundRoot");
            var child = Go("DisabledChild", false, root);
            director.SetGenericBinding(track, root.AddComponent<Animator>());
            var exposed = Go("Exposed", false);
            director.SetReferenceValue(new PropertyName("testTarget"), exposed);
            var scan = DisabledObjectCleanup.Collect();
            Assert.That(scan.Uncertainties, Is.Empty);
            Assert.That(Entry(scan, child).Reasons.Any(r => r.Contains("Timeline bound hierarchy")), Is.True);
            Assert.That(Entry(scan, exposed).Candidate, Is.False);
        }

        [Test]
        public void LoadedOtherScenesProtectReferencesButAreNotCleanupTargets()
        {
            var target = Go("Target", false);
            Scene other = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var outside = Go("Outside", false);
            outside.AddComponent<SceneScanFixture>().References = new Object[] { target };
            SceneManager.SetActiveScene(scene);
            var scan = DisabledObjectCleanup.Collect();
            Assert.That(scan.Entries.Any(e => e.Object == outside), Is.False);
            Assert.That(Entry(scan, target).Candidate, Is.False);
        }

        [Test]
        public void MissingScriptBlocksBatchAndExplainsWhy()
        {
            var missing = Go("Missing");
            var script = missing.AddComponent<SceneScanFixture>();
            using (var serialized = new SerializedObject(script))
            { serialized.FindProperty("m_Script").objectReferenceValue = null; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            var target = Go("Target", false);
            var scan = DisabledObjectCleanup.Collect();
            Assert.That(scan.Uncertainties.Any(s => s.Contains("Missing script")), Is.True);
            Assert.That(Entry(scan, target).Candidate, Is.False);
        }

        [Test]
        public void StaleReferenceOrActiveSceneChangePreventsMutation()
        {
            var target = Go("Target", false);
            var scan = DisabledObjectCleanup.Collect();
            Go("Holder").AddComponent<SceneScanFixture>().References = new Object[] { target };
            Assert.Throws<InvalidOperationException>(() => DisabledObjectCleanup.Apply(scan));
            Assert.That(target.tag, Is.EqualTo("Untagged"));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.Throws<InvalidOperationException>(() => DisabledObjectCleanup.Apply(scan));
        }

        [Test]
        public void ExclusionsUndoAndLaterUserTagsAreRespected()
        {
            var first = Go("First", false);
            var second = Go("Second", false);
            var scan = DisabledObjectCleanup.Collect();
            Entry(scan, second).Included = false;
            DisabledObjectCleanup.Apply(scan);
            Undo.FlushUndoRecordObjects();
            Assert.That(second.tag, Is.EqualTo("Untagged"));
            Undo.PerformUndo();
            Assert.That(first.tag, Is.EqualTo("Untagged"));
            var changes = DisabledObjectCleanup.Apply(scan);
            first.tag = "Respawn";
            Assert.That(DisabledObjectCleanup.Restore(changes), Is.Zero);
            Assert.That(first.tag, Is.EqualTo("Respawn"));
        }

        [Test]
        public void UdonLiveArrayReferencesProtectObjectsWithoutSerializingFirst()
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.Udon.UdonBehaviour")).FirstOrDefault(t => t != null);
            Assert.That(type, Is.Not.Null, "Native SDK fixture must be installed for this contract test.");
            var udon = Go("Udon").AddComponent(type);
            var target = Go("Target", false);
            object table = type.GetField("publicVariables").GetValue(udon);
            Type generic = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.Udon.Common.UdonVariable`1")).First(t => t != null);
            object variable = Activator.CreateInstance(generic.MakeGenericType(typeof(GameObject[])), "Targets", new[] { target });
            var contract = table.GetType().GetInterfaces().First(t => t.Name == "IUdonVariableTable");
            Assert.That(contract.GetMethod("TryAddVariable").Invoke(table, new[] { variable }), Is.True);
            var scan = DisabledObjectCleanup.Collect();
            Assert.That(scan.Uncertainties, Is.Empty);
            Assert.That(Entry(scan, target).Reasons.Any(r => r.Contains("Udon variable: Targets[0]")), Is.True);
        }

        [Test]
        public void SceneTexturesIncludeInactiveMaterialsSpritesTerrainScriptsAndAnimationSwaps()
        {
            Texture2D materialTexture = Texture("material"), scriptTexture = Texture("script"), swappedTexture = Texture("swap"), terrainTexture = Texture("terrain"), spriteTexture = Texture("sprite");
            var holder = Go("Inactive", false);
            holder.AddComponent<MeshRenderer>().sharedMaterial = Mat(materialTexture);
            holder.AddComponent<SceneScanFixture>().References = new Object[] { scriptTexture };
            var sprite = Sprite.Create(spriteTexture, new Rect(0, 0, 32, 32), Vector2.zero);
            temporary.Add(sprite);
            Go("Sprite", false).AddComponent<SpriteRenderer>().sprite = sprite;
            var data = new TerrainData(); var layer = new TerrainLayer { diffuseTexture = terrainTexture };
            temporary.Add(data); temporary.Add(layer);
            data.terrainLayers = new[] { layer };
            Go("Terrain").AddComponent<Terrain>().terrainData = data;
            var clip = new AnimationClip();
            temporary.Add(clip);
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(MeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = Mat(swappedTexture) } });
            Go("Animations").AddComponent<SceneScanFixture>().References = new Object[] { clip };
            var found = SceneReferenceScan.Collect(new[] { scene });
            Assert.That(found.Uncertainties, Is.Empty);
            Assert.That(found.Objects.OfType<Texture>(), Is.SupersetOf(new Texture[] { materialTexture, scriptTexture, swappedTexture, terrainTexture, spriteTexture }));
            var outsideTexture = Texture("outside");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Go("OtherScene").AddComponent<SceneScanFixture>().References = new Object[] { outsideTexture };
            Assert.That(SceneReferenceScan.Collect(new[] { scene }).Objects.Contains(outsideTexture), Is.False);
        }

        [Test]
        public void ReferencedPrefabChildTexturesAreDiscovered()
        {
            var texture = Texture("prefab");
            var material = Mat(texture);
            AssetDatabase.CreateAsset(material, folder + "/prefab.mat");
            var root = Go("PrefabRoot");
            Go("Child", true, root).AddComponent<MeshRenderer>().sharedMaterial = material;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/source.prefab");
            Object.DestroyImmediate(root);
            Go("Spawner").AddComponent<SceneScanFixture>().References = new Object[] { prefab };
            var found = SceneReferenceScan.Collect(new[] { scene });
            Assert.That(found.Uncertainties, Is.Empty);
            Assert.That(found.Objects.Contains(texture), Is.True);
        }

        [Test]
        public void SceneCrunchChangesQualityWithoutResizingAndRetainsQualityWhenDisabled()
        {
            var texture = Texture("crunch");
            string path = AssetDatabase.GetAssetPath(texture);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.maxTextureSize = 4096;
            var platform = importer.GetPlatformTextureSettings("Standalone");
            platform.overridden = true; platform.maxTextureSize = 2048; platform.format = TextureImporterFormat.DXT5;
            importer.SetPlatformTextureSettings(platform); importer.SaveAndReimport();
            byte[] source = File.ReadAllBytes(path);
            var entries = MaterialTextureBatch.CollectTextures(new[] { texture, texture }, MaterialTextureBatch.CrunchMode.Enable, 83);
            Assert.That(entries.Count, Is.EqualTo(1));
            var result = MaterialTextureBatch.Apply(entries);
            Assert.That(result.Failed, Is.Zero); Assert.That(result.Changed, Is.EqualTo(1));
            importer = (TextureImporter)AssetImporter.GetAtPath(path);
            Assert.That(importer.compressionQuality, Is.EqualTo(83));
            Assert.That(importer.maxTextureSize, Is.EqualTo(4096));
            Assert.That(importer.GetPlatformTextureSettings("Standalone").maxTextureSize, Is.EqualTo(2048));
            Assert.That(importer.GetPlatformTextureSettings("Standalone").compressionQuality, Is.EqualTo(83));
            MaterialTextureBatch.Apply(MaterialTextureBatch.CollectTextures(new[] { texture }, MaterialTextureBatch.CrunchMode.Enable, 27));
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(path)).compressionQuality, Is.EqualTo(27));
            MaterialTextureBatch.Apply(MaterialTextureBatch.CollectTextures(new[] { texture }, MaterialTextureBatch.CrunchMode.Disable, 50));
            importer = (TextureImporter)AssetImporter.GetAtPath(path);
            Assert.That(importer.crunchedCompression, Is.False);
            Assert.That(importer.compressionQuality, Is.EqualTo(27));
            Assert.That(importer.GetPlatformTextureSettings("Standalone").format, Is.EqualTo(TextureImporterFormat.DXT5));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(source));
        }

        [Test]
        public void CancellationAndNewMenusWork()
        {
            Go("Target", false);
            Assert.Throws<OperationCanceledException>(() => DisabledObjectCleanup.Collect(_ => true));
            Assert.That(EditorApplication.ExecuteMenuItem("Tools/Lightbulb/Scene Texture Crunch Compression"), Is.True);
            Assert.That(EditorApplication.ExecuteMenuItem("Tools/Lightbulb/Find Unreferenced Disabled Objects"), Is.True);
        }
    }
}
