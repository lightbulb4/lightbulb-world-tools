using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools
{
    internal static class LightmapVolumeSwap
    {
        internal const string Volumes = "_LightVolumesToggle", Specularity = "_LightVolumeSpecularity";
        const string Operation = "Swap lightmaps and Light Volumes";
        internal sealed class Preview
        {
            internal Scene scene;
            internal MeshRenderer[] renderers;
            internal Material[] materials;
            internal string fingerprint;
            internal int mixedRenderers, otherMaterialUsers;
        }

        internal static bool Matches(Material m) => m != null && m.shader != null && m.shader.name == "Mochie/Standard";
        internal static LightmapVolumeSwapState State(Scene scene) => scene.GetRootGameObjects()
            .SelectMany(g => g.GetComponentsInChildren<LightmapVolumeSwapState>(true)).SingleOrDefault();
        internal static float Scale(MeshRenderer renderer)
        {
            using (var so = new SerializedObject(renderer)) return so.FindProperty("m_ScaleInLightmap").floatValue;
        }
        static void SetScale(MeshRenderer renderer, float scale)
        {
            using (var so = new SerializedObject(renderer))
            {
                so.FindProperty("m_ScaleInLightmap").floatValue = scale;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }
        internal static void RequireReady(Scene scene)
        {
            // Existing guard checks edit mode, active saved scene, Unity and Bakery bakes.
            LightingExperiment.RequireScene(scene);
            var experiment = LightingExperiment.FindState(scene);
            if (experiment != null && !experiment.finished)
                throw new InvalidOperationException("This scene has an active Bakery LV3 Swapper experiment. Finish or undo that experiment before using this tool; both tools control the same surface settings.");
        }
        internal static Preview Scan(Scene scene)
        {
            RequireReady(scene);
            var all = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MeshRenderer>(true)).ToArray();
            var renderers = all.Where(r => (GameObjectUtility.GetStaticEditorFlags(r.gameObject) & StaticEditorFlags.ContributeGI) != 0
                && r.sharedMaterials.Any(Matches)).OrderBy(r => r.GetInstanceID()).ToArray();
            var materials = renderers.SelectMany(r => r.sharedMaterials).Where(Matches).Distinct().OrderBy(m => m.GetInstanceID()).ToArray();
            var preview = new Preview { scene = scene, renderers = renderers, materials = materials,
                mixedRenderers = renderers.Count(r => r.sharedMaterials.Any(m => m != null && !Matches(m))),
                otherMaterialUsers = Resources.FindObjectsOfTypeAll<Renderer>().Count(r => !EditorUtility.IsPersistent(r)
                    && r.gameObject.scene.IsValid() && !renderers.Contains(r) && r.sharedMaterials.Any(materials.Contains)) };
            // Include all slots and settings so the displayed plan cannot silently become stale.
            preview.fingerprint = string.Join(";", renderers.Select(r => r.GetInstanceID() + ":" + Scale(r).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                + ":" + (int)r.receiveGI + ":" + string.Join(",", r.sharedMaterials.Select(m => m == null ? 0 : m.GetInstanceID()))))
                + "|" + string.Join(";", materials.Select(m => m.GetInstanceID() + ":" + m.shader.GetInstanceID() + ":"
                    + (m.HasProperty(Volumes) ? m.GetFloat(Volumes).ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "missing") + ":"
                    + (m.HasProperty(Specularity) ? m.GetFloat(Specularity).ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "missing")));
            return preview;
        }
        static void ValidateMaterial(Material m, bool volumes, bool specularity)
        {
            if (!Matches(m)) throw new InvalidOperationException("A recorded material is missing or no longer uses Mochie/Standard.");
            if ((volumes && !m.HasProperty(Volumes)) || (specularity && !m.HasProperty(Specularity)))
                throw new InvalidOperationException(m.name + " does not expose the requested Light Volume properties.");
            string path = AssetDatabase.GetAssetPath(m);
            if (!string.IsNullOrEmpty(path) && (!path.StartsWith("Assets/", StringComparison.Ordinal) || !AssetDatabase.IsOpenForEdit(m)))
                throw new InvalidOperationException("Material must be editable under Assets: " + path);
        }
        internal static LightmapVolumeSwapState Apply(Preview preview, bool scale, bool volumes, bool specularity)
        {
            RequireReady(preview.scene);
            if (!scale && !volumes && !specularity) throw new InvalidOperationException("Choose at least one change.");
            if (preview.renderers.Length == 0) throw new InvalidOperationException("No matching GI-contributing Mochie/Standard renderers.");
            if (Scan(preview.scene).fingerprint != preview.fingerprint) throw new InvalidOperationException("Scene or material settings changed. Scan again before applying.");
            var state = State(preview.scene);
            if (state != null && state.applied) throw new InvalidOperationException("Restore the recorded settings before starting another swap.");
            if (volumes || specularity) foreach (var m in preview.materials) ValidateMaterial(m, volumes, specularity);
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(Operation);
            try
            {
                if (state == null)
                {
                    var go = new GameObject("Lightmap Volume Swap Settings") { tag = "EditorOnly" };
                    Undo.RegisterCreatedObjectUndo(go, Operation);
                    state = Undo.AddComponent<LightmapVolumeSwapState>(go);
                }
                Undo.RegisterCompleteObjectUndo(state, Operation);
                state.changeScale = scale; state.changeVolumes = volumes; state.changeSpecularity = specularity;
                state.surfaces = scale ? preview.renderers.Select(r => new LightmapVolumeSwapState.Surface { renderer = r, scale = Scale(r) }).ToList()
                    : new List<LightmapVolumeSwapState.Surface>();
                state.materials = volumes || specularity ? preview.materials.Select(m => new LightmapVolumeSwapState.MaterialSettings
                    { material = m, volumes = volumes ? m.GetFloat(Volumes) : 0, specularity = specularity ? m.GetFloat(Specularity) : 0 }).ToList()
                    : new List<LightmapVolumeSwapState.MaterialSettings>();
                Undo.RecordObjects(state.surfaces.Select(s => (Object)s.renderer).Concat(state.materials.Select(m => (Object)m.material)).ToArray(), Operation);
                foreach (var s in state.surfaces) SetScale(s.renderer, 0);
                foreach (var m in state.materials)
                {
                    if (volumes) m.material.SetFloat(Volumes, 1);
                    if (specularity) m.material.SetFloat(Specularity, 1);
                    EditorUtility.SetDirty(m.material);
                }
                state.applied = true; EditorUtility.SetDirty(state);
                EditorSceneManager.MarkSceneDirty(preview.scene);
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group);
                return state;
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }
        internal static void Restore(LightmapVolumeSwapState state)
        {
            if (state == null || !state.applied) throw new InvalidOperationException("No active swap to restore.");
            RequireReady(state.gameObject.scene);
            foreach (var s in state.surfaces)
                if (s.renderer == null || s.renderer.gameObject.scene != state.gameObject.scene)
                    throw new InvalidOperationException("A recorded renderer was removed or moved to another scene. Undo that change before restoring.");
            foreach (var m in state.materials) ValidateMaterial(m.material, state.changeVolumes, state.changeSpecularity);
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(Operation);
            try
            {
                Undo.RegisterCompleteObjectUndo(state, Operation);
                Undo.RecordObjects(state.surfaces.Select(s => (Object)s.renderer).Concat(state.materials.Select(m => (Object)m.material)).ToArray(), Operation);
                foreach (var s in state.surfaces) SetScale(s.renderer, s.scale);
                foreach (var m in state.materials)
                {
                    if (state.changeVolumes) m.material.SetFloat(Volumes, m.volumes);
                    if (state.changeSpecularity) m.material.SetFloat(Specularity, m.specularity);
                    EditorUtility.SetDirty(m.material);
                }
                state.applied = false; EditorUtility.SetDirty(state);
                EditorSceneManager.MarkSceneDirty(state.gameObject.scene);
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }
    }
}
