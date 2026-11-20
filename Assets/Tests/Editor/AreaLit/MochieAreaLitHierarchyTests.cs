using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using static Lightbulb.AreaLitOcclusion.MochieAreaLitHierarchy;
using Object = UnityEngine.Object;

namespace Lightbulb.AreaLitOcclusion.Tests
{
    public class MochieAreaLitHierarchyTests
    {
        private string folder;
        private GameObject root, outside;
        private Material parentMat, childMat, outsideMat;
        private RenderTexture data;
        private Texture2D image;
        private Texture2DArray array;
        private SlotEdit[] edits;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!Application.isBatchMode) Assert.Ignore("Runs against the batch test hierarchy.");
            folder = "Assets/__MochieAreaLit_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            string path = folder + "/Receiver.shader";
            File.WriteAllText(path, @"Shader ""Mochie/HierarchyTest"" { Properties {
                _AreaLitToggle (""Toggle"", Float) = 0
                _LightMesh (""Mesh"", 2D) = ""black"" {}
                _LightTex0 (""0"", 2D) = ""white"" {}
                _LightTex1 (""1"", 2D) = ""black"" {}
                _LightTex2 (""2"", 2D) = ""black"" {}
                _LightTex3 (""3+"", 2DArray) = """" {}
            } SubShader { Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local _AREALIT_ON
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return vertex; }
            float4 frag() : SV_Target { return 0; }
            ENDCG
            } } }");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            parentMat = Save(new Material(shader), "Parent.mat");
            childMat = Save(new Material(shader), "Child.mat");
            outsideMat = Save(new Material(shader), "Outside.mat");
            data = Save(new RenderTexture(8, 4, 0), "Mesh.renderTexture");
            image = Save(new Texture2D(2, 2), "Image.asset");
            array = Save(new Texture2DArray(2, 2, 1, TextureFormat.RGBA32, false), "Array.asset");
            root = new GameObject("Root", typeof(MeshRenderer));
            root.GetComponent<MeshRenderer>().sharedMaterials = new[] { parentMat, parentMat, null };
            var child = new GameObject("Child", typeof(SkinnedMeshRenderer));
            child.transform.SetParent(root.transform);
            child.GetComponent<SkinnedMeshRenderer>().sharedMaterials = new[] { childMat, parentMat };
            child.GetComponent<SkinnedMeshRenderer>().enabled = false;
            child.SetActive(false);
            outside = new GameObject("Outside", typeof(MeshRenderer));
            outside.GetComponent<MeshRenderer>().sharedMaterial = outsideMat;
            edits = new SlotEdit[AreaLitAuditBindings.ReceiverSlots.Length];
            yield return null;
            double deadline = EditorApplication.timeSinceStartup + 30;
            while ((EditorApplication.isUpdating || EditorApplication.isCompiling) && EditorApplication.timeSinceStartup < deadline)
                yield return null;
            Assert.That(AreaLitAudit.BlockedReason, Is.Null);
        }

        private T Save<T>(T asset, string file) where T : Object
        {
            AssetDatabase.CreateAsset(asset, folder + "/" + file);
            return asset;
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
            if (outside != null) Object.DestroyImmediate(outside);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        [Test]
        public void ChangesOnlySelectedFieldsOnUniqueReceiversAndUndoesTheBatch()
        {
            Assert.That(Collect(root), Is.EquivalentTo(new[] { parentMat, childMat }));
            parentMat.SetTexture("_LightTex0", image);
            childMat.SetTexture("_LightTex2", image);
            edits[0] = new SlotEdit { action = Assignment.Assign, texture = data };
            edits[2] = new SlotEdit { action = Assignment.Assign, texture = image };
            edits[3].action = Assignment.Clear;
            edits[4] = new SlotEdit { action = Assignment.Assign, texture = array };
            Assert.That(Apply(root, Toggle.Enable, edits, out var skipped), Is.EqualTo(2));
            Assert.That(skipped, Is.Empty);
            foreach (var material in new[] { parentMat, childMat })
            {
                Assert.That(material.GetInt(ToggleProperty), Is.EqualTo(1));
                Assert.That(material.IsKeywordEnabled(Keyword), Is.True);
                Assert.That(material.GetTexture("_LightMesh"), Is.EqualTo(data));
                Assert.That(material.GetTexture("_LightTex1"), Is.EqualTo(image));
                Assert.That(material.GetTexture("_LightTex2"), Is.Null);
                Assert.That(material.GetTexture("_LightTex3"), Is.EqualTo(array));
            }
            Assert.That(parentMat.GetTexture("_LightTex0"), Is.EqualTo(image));
            Assert.That(outsideMat.GetInt(ToggleProperty), Is.Zero);
            Assert.That(outsideMat.GetTexture("_LightMesh"), Is.Null);
            Assert.That(Apply(root, Toggle.Enable, edits, out _), Is.Zero);
            Undo.PerformUndo();
            Assert.That(parentMat.IsKeywordEnabled(Keyword), Is.False);
            Assert.That(parentMat.GetTexture("_LightMesh"), Is.Null);
            Assert.That(childMat.GetTexture("_LightTex2"), Is.EqualTo(image));
        }

        [Test]
        public void RejectsWrongTextureDimensionBeforeWritingAndRepairsToggleKeywordMismatch()
        {
            edits[4] = new SlotEdit { action = Assignment.Assign, texture = image };
            Assert.Throws<InvalidOperationException>(() => Apply(root, Toggle.Enable, edits, out _));
            Assert.That(parentMat.GetInt(ToggleProperty), Is.Zero);
            edits[4] = default;
            childMat.EnableKeyword(Keyword);
            Assert.That(Apply(root, Toggle.Disable, edits, out _), Is.EqualTo(1));
            Assert.That(childMat.IsKeywordEnabled(Keyword), Is.False);
        }

        [Test]
        public void AssignsWithToggleKeptAndReportsNonEditableMaterials()
        {
            var temporary = new Material(parentMat);
            try
            {
                var builtin = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
                root.GetComponent<MeshRenderer>().sharedMaterials = new[] { parentMat, builtin, temporary };
                Assert.That(Collect(root), Is.EquivalentTo(new[] { parentMat, childMat, temporary }));
                edits[1] = new SlotEdit { action = Assignment.Assign, texture = image };
                Assert.That(Apply(root, Toggle.Keep, edits, out var skipped), Is.EqualTo(2));
                Assert.That(skipped.Count, Is.EqualTo(1));
                Assert.That(temporary.GetTexture("_LightTex0"), Is.Null);
                Assert.That(parentMat.GetTexture("_LightTex0"), Is.EqualTo(image));
                Assert.That(parentMat.GetInt(ToggleProperty), Is.Zero);
            }
            finally { Object.DestroyImmediate(temporary); }
        }

        [Test]
        public void HierarchyMenuOpensWithTheSelectedRoot()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("Opening an editor window requires a graphics device.");
            var selection = Selection.activeObject;
            try
            {
                Selection.activeGameObject = root;
                Assert.That(EditorApplication.ExecuteMenuItem(MochieAreaLitHierarchyWindow.MenuPath), Is.True);
                var window = Resources.FindObjectsOfTypeAll<MochieAreaLitHierarchyWindow>().Single();
                Assert.That(new SerializedObject(window).FindProperty("root").objectReferenceValue, Is.EqualTo(root));
                window.Close();
            }
            finally { Selection.activeObject = selection; }
        }
    }
}
