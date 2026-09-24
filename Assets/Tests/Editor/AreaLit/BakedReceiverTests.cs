using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.AreaLitOcclusion.Tests
{
    public class BakedReceiverTests
    {
        string folder, originalScene;
        AreaLitOcclusionBakeController.BakeOutputPlan lastPlan;
        string[] originalMaterials;
        Material material;
        Texture2D oldMap, mapA, mapB;
        AreaLitOcclusionJournal journal;
        readonly Vector4 uvA = new Vector4(.25f, .5f, .1f, .2f);
        readonly Vector4 uvB = new Vector4(.5f, .25f, .3f, .4f);
        [SetUp] public void Setup()
        {
            if (!Application.isBatchMode) Assert.Ignore("Uses temporary scenes in the isolated batch test project.");
            folder = "Assets/__AreaLitReceiverTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            originalScene = folder + "/Original.unity";
            EditorSceneManager.SaveScene(scene, originalScene);
            File.WriteAllText(folder + "/Receiver.shader", "Shader \"Hidden/AreaLitReceiverTest\" { Properties { _AreaLitOcclusion(\"Map\",2D)=\"white\"{} _AreaLitOcclusionUVSet(\"UV\",Float)=0 _AreaLitToggle(\"Toggle\",Float)=1 } SubShader { Pass {} } }");
            AssetDatabase.ImportAsset(folder + "/Receiver.shader", ImportAssetOptions.ForceSynchronousImport);
            material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(folder + "/Receiver.shader"));
            Assert.That(material.HasProperty(AreaLitOcclusionUvTools.AreaLitOcclusionUvSetProperty), Is.True);
            AssetDatabase.CreateAsset(material, folder + "/Receiver.mat");
            oldMap = Map("Old"); mapA = Map("A"); mapB = Map("B");
            material.SetTexture("_AreaLitOcclusion", oldMap);
            AssetDatabase.SaveAssets();
            originalMaterials = AssetDatabase.FindAssets("t:Material");
            journal = new AreaLitOcclusionJournal { transactionId = Guid.NewGuid().ToString("N"), state = "Publishing",
                transactionAssetPath = folder, bakeOutputAssetPath = folder, applyToMaterials = true };
        }
        Texture2D Map(string name)
        {
            var texture = new Texture2D(2, 2); AssetDatabase.CreateAsset(texture, folder + "/" + name + ".asset"); return texture;
        }
        MeshRenderer Make(string name)
        {
            var renderer = GameObject.CreatePrimitive(PrimitiveType.Cube).GetComponent<MeshRenderer>();
            renderer.name = name; renderer.sharedMaterial = material; return renderer;
        }
        AreaLitOcclusionBakeController.BakedReceiver Entry(MeshRenderer r, Vector4 uv, Texture2D map)
        {
            return new AreaLitOcclusionBakeController.BakedReceiver { locator = AreaLitOcclusionDiscovery.CreateLocator(r),
                materialPath = AssetDatabase.GetAssetPath(material), sourcePath = AssetDatabase.GetAssetPath(map), slot = 0, scaleOffset = uv };
        }
        int Apply(params AreaLitOcclusionBakeController.BakedReceiver[] entries)
        {
            var plan = lastPlan = new AreaLitOcclusionBakeController.BakeOutputPlan(); plan.receivers.AddRange(entries);
            return AreaLitOcclusionBakeController.ApplyMaterials(journal, plan, new Dictionary<string, string> {
                {AssetDatabase.GetAssetPath(mapA), AssetDatabase.GetAssetPath(mapA)}, {AssetDatabase.GetAssetPath(mapB), AssetDatabase.GetAssetPath(mapB)} });
        }
        void AssertUv(Material m, Vector4 uv)
        {
            Assert.That(m.GetTextureScale("_AreaLitOcclusion"), Is.EqualTo(new Vector2(uv.x, uv.y)));
            Assert.That(m.GetTextureOffset("_AreaLitOcclusion"), Is.EqualTo(new Vector2(uv.z, uv.w)));
            Assert.That(m.GetFloat(AreaLitOcclusionUvTools.AreaLitOcclusionUvSetProperty), Is.EqualTo(1));
        }
        [Test] public void CapturesStagingAtlasBeforeRestoringOriginalScenes()
        {
            var r = Make("Receiver"); EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            var staging = folder + "/Staging.unity";
            Assert.That(AssetDatabase.CopyAsset(originalScene, staging), Is.True);
            EditorSceneManager.OpenScene(staging);
            r = UnityEngine.Object.FindObjectOfType<MeshRenderer>(); r.lightmapIndex = 0; r.lightmapScaleOffset = uvA;
            LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = mapA } };
            journal.sceneCopies.Add(new JournalSceneCopy { sourcePath = originalScene, stagingPath = staging });
            journal.receiverMaterialPaths.Add(AssetDatabase.GetAssetPath(material));
            var plan = AreaLitOcclusionBakeController.CollectBakeOutputPlan(journal);
            Assert.That(plan.receivers.Count, Is.EqualTo(1));
            EditorSceneManager.OpenScene(originalScene);
            r = UnityEngine.Object.FindObjectOfType<MeshRenderer>(); r.lightmapScaleOffset = uvB;
            Apply(plan.receivers.ToArray()); AssertUv(material, uvA);
            Assert.That(material.GetTexture("_AreaLitOcclusion"), Is.EqualTo(mapA));
        }
        [Test] public void TextureOptOutStillSetsUvsAndSupportsUndo()
        {
            var r = Make("Receiver"); journal.applyToMaterials = false;
            Apply(Entry(r, uvA, mapA)); AssertUv(material, uvA);
            Assert.That(material.GetTexture("_AreaLitOcclusion"), Is.EqualTo(oldMap));
            Undo.PerformUndo(); Assert.That(material.GetTextureScale("_AreaLitOcclusion"), Is.EqualTo(Vector2.one));
        }
        [TestCase(false)]
        [TestCase(true)]
        public void ConflictingCoordinatesStayUnchangedWithoutVariants(bool differentAtlases)
        {
            var a = Make("A"); var b = Make("B");
            Assert.That(Apply(Entry(a, uvA, mapA), Entry(b, uvB, differentAtlases ? mapB : mapA)),
                Is.EqualTo(differentAtlases ? 0 : 1));
            Assert.That(a.sharedMaterial, Is.EqualTo(material)); Assert.That(b.sharedMaterial, Is.EqualTo(material));
            Assert.That(material.GetTextureScale("_AreaLitOcclusion"), Is.EqualTo(Vector2.one));
            Assert.That(material.GetTextureOffset("_AreaLitOcclusion"), Is.EqualTo(Vector2.zero));
            Assert.That(material.GetFloat(AreaLitOcclusionUvTools.AreaLitOcclusionUvSetProperty), Is.Zero);
            Assert.That(material.GetTexture("_AreaLitOcclusion"), Is.EqualTo(differentAtlases ? oldMap : mapA));
            Assert.That(lastPlan.warnings.Exists(w => w.Contains("UV settings left unchanged")), Is.True);
        }
        [Test] public void AmbiguousAtlasPreservesTextureButUpdatesUnambiguousUvs()
        {
            var a = Make("A"); var b = Make("B");
            Apply(Entry(a, uvA, mapA), Entry(b, uvA, mapB));
            AssertUv(material, uvA);
            Assert.That(material.GetTexture("_AreaLitOcclusion"), Is.EqualTo(oldMap));
            Assert.That(a.sharedMaterial, Is.EqualTo(material)); Assert.That(b.sharedMaterial, Is.EqualTo(material));
            Assert.That(lastPlan.warnings.Exists(w => w.Contains("occlusion texture left unchanged")), Is.True);
        }
        [Test] public void UnbakedUsersPreventSharedMaterialUpdates()
        {
            var baked = Make("Baked"); var unbaked = Make("Unbaked");
            Assert.That(Apply(Entry(baked, uvA, mapA)), Is.Zero);
            Assert.That(baked.sharedMaterial, Is.EqualTo(material)); Assert.That(unbaked.sharedMaterial, Is.EqualTo(material));
            Assert.That(material.GetTexture("_AreaLitOcclusion"), Is.EqualTo(oldMap));
            Assert.That(material.GetTextureScale("_AreaLitOcclusion"), Is.EqualTo(Vector2.one));
            Assert.That(lastPlan.warnings.Exists(w => w.Contains("no baked mapping")), Is.True);
        }
        [Test] public void MissingPublishedAtlasFailsBeforeChangingMaterial()
        {
            var r = Make("Receiver"); var entry = Entry(r, uvA, mapA); entry.sourcePath = "missing";
            Assert.Throws<InvalidOperationException>(() => Apply(entry));
            Assert.That(material.GetTexture("_AreaLitOcclusion"), Is.EqualTo(oldMap));
            Assert.That(material.GetTextureScale("_AreaLitOcclusion"), Is.EqualTo(Vector2.one));
        }
        [TearDown] public void Cleanup()
        {
            if (folder == null) return;
            CollectionAssert.AreEquivalent(originalMaterials, AssetDatabase.FindAssets("t:Material"), "Bake application must never generate materials.");
            Undo.ClearAll(); LightmapSettings.lightmaps = Array.Empty<LightmapData>();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(folder);
            foreach (var suffix in new[] { "", ".previous", ".tmp" })
                if (File.Exists(AreaLitOcclusionJournalStore.ActivePath + suffix)) File.Delete(AreaLitOcclusionJournalStore.ActivePath + suffix);
            folder = null;
        }
    }
}
