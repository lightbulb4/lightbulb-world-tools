using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using static Lightbulb.WorldTools.LightingExperimentState;
using static Lightbulb.WorldTools.LightingExperimentAdapter;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools
{
    internal static class LightingExperiment
    {
        const string Operation = "Change lighting experiment";
        internal sealed class Preview
        {
            internal Scene scene;
            internal List<LightingExperimentConversion.Candidate> candidates = new List<LightingExperimentConversion.Candidate>();
            internal List<Bounds> volumes;
            internal Bounds world;
            internal List<string> warnings = new List<string>();
            internal float brightness = 1, density = 1;
            internal string fingerprint;
            internal bool fromBakery;
        }
        internal static List<Component> Components(Scene scene) => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Component>(true)).Where(c => c != null).ToList();
        internal static LightingExperimentState FindState(Scene scene) => Components(scene).OfType<LightingExperimentState>().SingleOrDefault();
        internal static void RequireScene(Scene scene)
        {
            SceneMaterials.RequireActive(scene);
            if (SceneManager.sceneCount != 1) throw new InvalidOperationException("Open only the experiment scene. Lighting globals and the Light Volume Manager are shared across loaded scenes.");
            if (string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Save this scene before creating an experiment.");
            if (Lightmapping.isRunning) throw new InvalidOperationException("Wait for the lighting bake to finish.");
            Type bakery = Find("ftRenderLightmap");
            var inProgress = bakery?.GetField("bakeInProgress", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (inProgress != null && inProgress.FieldType == typeof(bool) && (bool)inProgress.GetValue(null)) throw new InvalidOperationException("Wait for Bakery to finish.");
        }
        static string Fingerprint(Scene scene) => string.Join("|", Components(scene).Where(c => IsBakery(c) || c.GetType().Namespace == "VRCLightVolumes" || c is Renderer).Select(c => c.GetInstanceID() + ":" + EditorJsonUtility.ToJson(c) + ":" + c.transform.localToWorldMatrix));
        internal static Preview Scan(bool fromBakery, bool forcePoint, int maxVolumes, float padding, float density, int layers)
        {
            Scene scene = SceneMaterials.Active(); RequireScene(scene);
            var adapter = new LightingExperimentAdapter();
            var components = Components(scene);
            if (components.Count(c => c.GetType() == adapter.Manager) > 1) throw new InvalidOperationException("Resolve multiple Light Volume Managers before creating an experiment.");
            foreach (Component volume in components.Where(c => c.GetType() == adapter.Volume))
                if (volume.GetComponentInChildren<Renderer>(true) != null || volume.GetComponentInChildren<Terrain>(true) != null || volume.GetComponentInChildren(adapter.Manager, true) != null || volume.GetComponentInChildren<Light>(true) != null || volume.GetComponentInChildren<Camera>(true) != null || volume.GetComponentInChildren<ReflectionProbe>(true) != null || volume.GetComponentInChildren(adapter.Point, true) != null || volume.GetComponentsInChildren(adapter.Volume, true).Length > 1)
                    throw new InvalidOperationException("Regular volume '" + volume.name + "' contains world geometry, cameras or other lights/volumes. Put regular volumes on dedicated objects so excluding them from baking cannot hide other scene content.");
            if (components.Any(c => c.GetType().FullName == "VRCLightVolumes.LightVolumeSetup" || c.GetType().FullName == "VRCLightVolumes.PointLightVolume" || c.GetType().FullName == "VRCLightVolumes.LightVolume"))
                throw new InvalidOperationException("Migrate legacy Light Volumes components with Light Volumes' own migration tool first.");
            var geometry = components.OfType<MeshRenderer>().Where(r => (layers & (1 << r.gameObject.layer)) != 0 && r.GetComponent<MeshFilter>()?.sharedMesh != null).Select(r => r.bounds).ToList();
            var preview = new Preview { scene = scene, world = LightingVolumeBounds.Enclose(geometry), density = density, fromBakery = fromBakery };
            preview.volumes = LightingVolumeBounds.Fit(geometry, maxVolumes, padding);
            LightingExperimentState state = FindState(scene);
            foreach (Component c in components.Where(c => fromBakery ? IsBakery(c) : c.GetType() == adapter.Point).OrderBy(c => GlobalObjectId.GetGlobalObjectIdSlow(c).ToString(), StringComparer.Ordinal))
            {
                if (state != null && state.lights.Any(l => l.component == c && l.counterpart != null)) continue;
                preview.candidates.Add(LightingExperimentConversion.Inspect(c, forcePoint));
            }
            foreach (Material material in SceneMaterials.Collect(scene))
                if (!SupportedMaterial(material)) preview.warnings.Add(material.name + ": shader '" + material.shader?.name + "' has no verified routing adapter; its lighting is not controlled.");
            foreach (Terrain terrain in components.OfType<Terrain>()) preview.warnings.Add(terrain.name + ": terrain lightmap assignments are controlled, but its material lighting is not routed.");
            int points = components.Count(c => c.GetType() == adapter.Point) + preview.candidates.Count(c => !c.toBakery && c.included);
            if (points > 128) preview.warnings.Add(points + " Point Light Volumes exceed the installed manager's 128-light upload limit. Use per-light inclusion and reduce overlap.");
            preview.warnings.Add("Brightness and falloff are estimates. Sky/sun/mesh → point conversion discards source shape. Regular volume placement uses mesh bounds, not room detection.");
            preview.fingerprint = Fingerprint(scene);
            return preview;
        }
        internal static bool SupportedMaterial(Material m) => m != null && m.shader != null && (m.shader.name == "Mochie/Standard" || m.shader.name == "Mochie/Standard Lite") && m.HasProperty("_LightVolumeSpecularity") && m.HasProperty("_AdditiveLightVolumeStrength");
        internal static LightingExperimentState Create(Preview preview, bool addVolumes)
        {
            RequireScene(preview.scene);
            if (FindState(preview.scene) != null) throw new InvalidOperationException("This scene already has an experiment. Add missing counterparts instead.");
            if (preview.fingerprint != Fingerprint(preview.scene)) throw new InvalidOperationException("The scene changed since preview. Scan again.");
            if (!float.IsFinite(preview.density) || preview.density <= 0 || preview.density > 15) throw new InvalidOperationException("Choose a voxel density greater than zero and at most 15.");
            double estimatedBytes = preview.volumes.Sum(b => Math.Ceiling(b.size.x * preview.density) * Math.Ceiling(b.size.y * preview.density) * Math.Ceiling(b.size.z * preview.density) * 24);
            if (addVolumes && estimatedBytes > 1024d * 1024 * 1024) throw new InvalidOperationException("Proposed volumes exceed 1 GiB of raw SH data. Lower density or restrict geometry layers before creation.");
            var adapter = new LightingExperimentAdapter();
            int group = Begin();
            try
            {
                string materialFolder = CreateFolder("Assets/LightbulbLightingExperiments", preview.scene.name);
                string backupScene = materialFolder + "/BeforeExperiment.unity";
                if (!EditorSceneManager.SaveScene(preview.scene, backupScene, true)) throw new InvalidOperationException("Could not save the pre-experiment scene backup. Nothing was converted.");
                var go = new GameObject("Lightbulb Lighting Experiment"); go.tag = "EditorOnly";
                Undo.RegisterCreatedObjectUndo(go, Operation);
                var state = Undo.AddComponent<LightingExperimentState>(go);
                state.materialFolder = materialFolder; state.backupScene = backupScene;
                Record(state);
                CaptureSurfaces(state);
                CloneMaterials(state);
                foreach (Component c in Components(preview.scene)) Track(state, c, adapter);
                Component oldManager = Components(preview.scene).FirstOrDefault(c => c.GetType() == adapter.Manager);
                if (oldManager != null) Undo.RegisterFullObjectHierarchyUndo(oldManager.gameObject, Operation);
                Convert(preview, state, adapter);
                if (addVolumes && !state.lights.Any(l => l.family == Family.BakedVolume))
                    foreach (Bounds bounds in preview.volumes)
                    {
                        var volume = adapter.Create(false, "Experiment Volume " + (state.lights.Count(l => l.family == Family.BakedVolume) + 1));
                        volume.transform.position = bounds.center; volume.transform.localScale = bounds.size;
                        Set(volume, "VoxelsPerUnit", preview.density); Set(volume, "Bake", true);
                        adapter.Sync(volume);
                        Track(state, volume, adapter).generatedObject = volume.gameObject;
                    }
                // A point or regular volume's native creation path also creates a valid manager.
                state.manager = Components(preview.scene).FirstOrDefault(c => c.GetType() == adapter.Manager);
                if (state.manager == null)
                {
                    state.manager = adapter.CreateManager(preview.scene);
                    if (state.manager == null) throw new InvalidOperationException("Native Light Volume Manager creation failed.");
                }
                state.managerEnabled = ((Behaviour)state.manager).enabled;
                state.managerProbeBlending = Get<bool>(state.manager, "LightProbesBlending");
                if (oldManager == null) state.generatedManager = state.manager.gameObject;
                // Start in the user's original Bakery configuration. Conversion is not a mode switch.
                SetModeFields(state, preview.fromBakery ? Mode.Bakery : Mode.LightVolumes);
                ApplyCore(state, adapter);
                EditorSceneManager.MarkSceneDirty(preview.scene);
                Undo.CollapseUndoOperations(group);
                return state;
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }
        static string CreateFolder(string root, string sceneName)
        {
            if (!AssetDatabase.IsValidFolder(root)) AssetDatabase.CreateFolder("Assets", "LightbulbLightingExperiments");
            string path = AssetDatabase.GenerateUniqueAssetPath(root + "/" + sceneName.Replace('/', '_'));
            AssetDatabase.CreateFolder(root, System.IO.Path.GetFileName(path));
            return path;
        }
        static void CaptureSurfaces(LightingExperimentState state)
        {
            foreach (Component c in Components(state.gameObject.scene))
            {
                if (c is MeshRenderer renderer)
                {
                    var so = new SerializedObject(renderer); var scale = so.FindProperty("m_ScaleInLightmap");
                    if (scale == null) continue;
                    state.surfaces.Add(new SurfaceState { renderer = renderer, scale = scale.floatValue, lightmapIndex = renderer.lightmapIndex, lightmapST = renderer.lightmapScaleOffset, receiveGI = renderer.receiveGI, probes = renderer.lightProbeUsage });
                }
                if (c is Terrain terrain)
                {
                    var scale = new SerializedObject(terrain).FindProperty("m_ScaleInLightmap");
                    if (scale != null) state.surfaces.Add(new SurfaceState { terrain = terrain, scale = scale.floatValue, lightmapIndex = terrain.lightmapIndex, lightmapST = terrain.lightmapScaleOffset });
                }
                if (c is ReflectionProbe probe) { state.reflectionProbes.Add(probe); state.reflectionEnabled.Add(probe.enabled); }
                if (c is Light light) { state.unityLights.Add(light); state.unityEnabled.Add(light.enabled); }
            }
        }
        static readonly string[] Routing = { "_LightVolumesToggle", "_AdditiveLightVolumesToggle", "_LightVolumeStrength", "_AdditiveLightVolumeStrength", "_LightVolumeSpecularity", "_LightVolumeSpecularityStrength", "_BAKERY_LMSPEC", "_BakeryLMSpecStrength", "_ReflectionsToggle" };
        static void CloneMaterials(LightingExperimentState state)
        {
            var map = new Dictionary<Material, Material>();
            foreach (Renderer renderer in Components(state.gameObject.scene).OfType<Renderer>())
            {
                Material[] original = renderer.sharedMaterials, assigned = original.ToArray(); bool changed = false;
                for (int i = 0; i < assigned.Length; i++)
                {
                    Material material = assigned[i]; if (!SupportedMaterial(material)) continue;
                    if (!map.TryGetValue(material, out Material clone))
                    {
                        clone = new Material(material) { name = material.name + " [Experiment]" };
                        string safeName = string.Concat(material.name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                        AssetDatabase.CreateAsset(clone, AssetDatabase.GenerateUniqueAssetPath(state.materialFolder + "/" + safeName + ".mat"));
                        var saved = new MaterialState { original = material, experiment = clone };
                        foreach (string property in Routing.Where(material.HasProperty)) { saved.properties.Add(property); saved.values.Add(material.GetFloat(property)); }
                        state.materials.Add(saved); map.Add(material, clone);
                    }
                    assigned[i] = clone; changed = true;
                }
                if (!changed) continue;
                Undo.RecordObject(renderer, Operation); state.assignments.Add(new Assignment { renderer = renderer, original = original });
                renderer.sharedMaterials = assigned; PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
        }
        internal static LightState Track(LightingExperimentState state, Component component, LightingExperimentAdapter adapter)
        {
            if (!IsBakery(component) && component.GetType() != adapter.Point && component.GetType() != adapter.Volume) return null;
            var existing = state.lights.FirstOrDefault(l => l.component == component); if (existing != null) return existing;
            var light = new LightState { component = component, family = IsBakery(component) ? Family.Bakery : component.GetType() == adapter.Point ? Family.PointVolume : Family.BakedVolume };
            CaptureLight(light, true); state.lights.Add(light); return light;
        }
        static void CaptureLight(LightState light, bool captureShadows)
        {
            if (light.component == null || light.gated) return;
            light.enabled = ((Behaviour)light.component).enabled;
            light.activeSelf = light.component.gameObject.activeSelf;
            light.intensity = Get<float>(light.component, light.family == Family.Bakery ? "intensity" : "Intensity");
            if (light.family == Family.BakedVolume) light.bake = Get<bool>(light.component, "Bake");
            if (light.family == Family.PointVolume)
            {
                light.bakeIntoProbes = Get<bool>(light.component, "BakeIntoProbes");
                if (captureShadows) { light.shadows = Get<bool>(light.component, "Shadows"); light.rebakeShadows = Get<bool>(light.component, "RebakeShadows"); }
            }
        }
        static void Convert(Preview preview, LightingExperimentState state, LightingExperimentAdapter adapter)
        {
            foreach (var c in preview.candidates.Where(c => c.included && c.problem == null))
            {
                LightState original = Track(state, c.source, adapter);
                if (original.counterpart != null) continue;
                if (original.gated) throw new InvalidOperationException("Activate the source setup before adding counterparts, so authoring values are available.");
                Component created = LightingExperimentConversion.Create(c, adapter, preview.world, preview.brightness, state.materialFolder);
                LightState counterpart = Track(state, created, adapter);
                counterpart.counterpart = original.component; counterpart.generatedObject = created.gameObject; original.counterpart = created;
            }
        }
        internal static void AddCounterparts(Preview preview, LightingExperimentState state)
        {
            Require(state, true);
            if (preview.scene != state.gameObject.scene || preview.fingerprint != Fingerprint(preview.scene)) throw new InvalidOperationException("Scene changed. Scan again.");
            var adapter = new LightingExperimentAdapter(); int group = Begin();
            try
            {
                Record(state);
                foreach (Component c in Components(preview.scene).Where(c => IsBakery(c) || c.GetType() == adapter.Point || c.GetType() == adapter.Volume))
                    if (!state.lights.Any(l => l.component == c)) { Undo.RegisterFullObjectHierarchyUndo(c.gameObject, Operation); Track(state, c, adapter); }
                Convert(preview, state, adapter); ApplyCore(state, adapter); Undo.CollapseUndoOperations(group);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }
        internal static void SetModeFields(LightingExperimentState state, Mode mode)
        {
            state.mode = mode; state.bakeryLights = mode != Mode.LightVolumes; state.pointLights = mode != Mode.Bakery;
            state.bakedVolumes = mode == Mode.Hybrid; state.useLightmaps = mode != Mode.LightVolumes;
        }
        internal static void Switch(LightingExperimentState state, Mode mode)
        {
            Require(state); var adapter = new LightingExperimentAdapter(); int group = Begin();
            try { Record(state); SetModeFields(state, mode); ApplyCore(state, adapter); Undo.CollapseUndoOperations(group); }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }
        internal static void Apply(LightingExperimentState state)
        {
            Require(state); var adapter = new LightingExperimentAdapter(); int group = Begin();
            try { Record(state); ApplyCore(state, adapter); Undo.CollapseUndoOperations(group); }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }
        internal static void Require(LightingExperimentState state, bool allowNewLights = false)
        {
            if (state == null || state.finished) throw new InvalidOperationException("Create an active lighting experiment first.");
            RequireScene(state.gameObject.scene);
            if (state.schema != 1 || !state.CompareTag("EditorOnly")) throw new InvalidOperationException("Invalid experiment state or missing EditorOnly tag.");
            if (state.manager == null) throw new InvalidOperationException("The experiment's Light Volume Manager was removed. Undo that deletion before continuing.");
            if (state.lights.Any(l => l.component == null)) throw new InvalidOperationException("An experiment light was deleted. Undo the deletion before switching or finalizing.");
            if (state.lights.Any(l => l.component.gameObject.scene != state.gameObject.scene)) throw new InvalidOperationException("An experiment light moved to another scene.");
            if (!allowNewLights && Components(state.gameObject.scene).Any(c => (IsBakery(c) || c.GetType().FullName == "VRCLightVolumes.PointLightVolumeInstance" || c.GetType().FullName == "VRCLightVolumes.LightVolumeInstance") && !state.lights.Any(l => l.component == c)))
                throw new InvalidOperationException("New scene lights were found. Preview Create missing counterparts and apply that preview to register them; deselect conversions if you only want to adopt their existing settings.");
        }
        static int Begin() { Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName(Operation); return group; }
        static void Record(LightingExperimentState state)
        {
            Undo.RegisterCompleteObjectUndo(state, Operation);
            foreach (var light in state.lights.Where(l => l.component != null)) Undo.RegisterFullObjectHierarchyUndo(light.component.gameObject, Operation);
            if (state.manager != null) Undo.RegisterFullObjectHierarchyUndo(state.manager.gameObject, Operation);
            foreach (var surface in state.surfaces) { if (surface.renderer != null) Undo.RecordObject(surface.renderer, Operation); if (surface.terrain != null) Undo.RecordObject(surface.terrain, Operation); }
            foreach (var material in state.materials) if (material.experiment != null) Undo.RecordObject(material.experiment, Operation);
            foreach (var probe in state.reflectionProbes) if (probe != null) Undo.RecordObject(probe, Operation);
            foreach (var light in state.unityLights) if (light != null) Undo.RecordObject(light, Operation);
        }
        static void ApplyCore(LightingExperimentState state, LightingExperimentAdapter adapter)
        {
            if (!state.useLightmaps && !state.lvDiffuse && state.materials.Count > 0) throw new InvalidOperationException("LV specular-only requires Bakery lightmaps in the installed Mochie shader. Enable Use Bakery lightmaps or LV diffuse.");
            if (state.materials.Any(m => m.experiment == null)) throw new InvalidOperationException("An experiment material was deleted. Restore it before switching.");
            foreach (LightState light in state.lights)
            {
                bool wanted = light.included && (light.family == Family.Bakery ? state.bakeryLights : light.family == Family.PointVolume ? state.pointLights : state.bakedVolumes);
                // Only the gate's own fields are stored. Color, cookies, transforms, etc. stay on
                // the real component and are never reconstructed from a previous conversion.
                CaptureLight(light, state.appliedShadows);
                if (!float.IsFinite(light.intensity)) throw new InvalidOperationException("Invalid intensity on " + light.component.name);
                bool parentActive = light.component.transform.parent == null || light.component.transform.parent.gameObject.activeInHierarchy;
                wanted = wanted && light.enabled && (light.family == Family.BakedVolume ? light.activeSelf && parentActive : light.component.gameObject.activeInHierarchy);
                var behaviour = (Behaviour)light.component;
                behaviour.enabled = wanted && light.enabled;
                Set(light.component, light.family == Family.Bakery ? "intensity" : "Intensity", wanted ? light.intensity : 0f);
                // Native LV authoring deletes its Bakery helper when Bake becomes false. Keep
                // that configured flag intact and exclude the dedicated volume object instead.
                // Both native regular-volume bake paths explicitly require activeInHierarchy.
                if (light.family == Family.BakedVolume) light.component.gameObject.SetActive(wanted);
                if (light.family == Family.PointVolume)
                {
                    Set(light.component, "Shadows", wanted && light.enabled && state.lvShadows && light.shadows);
                    Set(light.component, "RebakeShadows", wanted && light.enabled && state.lvShadows && light.rebakeShadows);
                    Set(light.component, "BakeIntoProbes", wanted && light.enabled && light.bakeIntoProbes);
                }
                if (light.family != Family.Bakery)
                {
                    Set(light.component, "IsActive", wanted && behaviour.isActiveAndEnabled && light.intensity != 0);
                    adapter.Sync(light.component);
                }
                if (light.generatedObject != null && light.family == Family.Bakery && light.component.GetType().Name == "BakeryLightMesh")
                {
                    Renderer emitter = light.generatedObject.GetComponent<Renderer>();
                    if (emitter != null) { if (!light.gated) light.generatedRendererEnabled = emitter.enabled; emitter.enabled = wanted && light.generatedRendererEnabled; }
                }
                light.gated = !wanted;
            }
            bool anyLV = state.lights.Any(l => l.family != Family.Bakery && !l.gated && l.enabled && l.component.gameObject.activeInHierarchy);
            if (state.manager != null)
            {
                ((Behaviour)state.manager).enabled = anyLV;
                Set(state.manager, "LightProbesBlending", state.useLightmaps && state.managerProbeBlending);
                adapter.Sync(state.manager); adapter.Refresh(state.manager);
            }
            if (!anyLV) Shader.SetGlobalFloat("_UdonLightVolumeEnabled", 0);
            foreach (LightState light in state.lights.Where(l => l.gated && l.family != Family.Bakery))
                if (Get<bool>(light.component, "IsActive") || Get<float>(light.component, "Intensity") != 0) throw new InvalidOperationException("Light Volumes retained an excluded light: " + light.component.name);
            ApplySurfaces(state);
            foreach (MaterialState material in state.materials) RouteMaterial(material, state, anyLV);
            for (int i = 0; i < state.reflectionProbes.Count; i++)
            {
                ReflectionProbe probe = state.reflectionProbes[i]; if (probe == null) continue;
                if (!state.reflectionGated) state.reflectionEnabled[i] = probe.enabled;
                probe.enabled = (state.useLightmaps || state.keepReflectionProbes) && state.reflectionEnabled[i];
            }
            state.reflectionGated = !state.useLightmaps && !state.keepReflectionProbes;
            for (int i = 0; i < state.unityLights.Count; i++)
            {
                Light light = state.unityLights[i]; if (light == null) continue;
                if (!state.unityGated) state.unityEnabled[i] = light.enabled;
                light.enabled = state.bakeryLights && state.unityEnabled[i];
                PrefabUtility.RecordPrefabInstancePropertyModifications(light);
            }
            state.unityGated = !state.bakeryLights;
            state.appliedShadows = state.lvShadows;
            state.status = "Bake required. Mode switches preserve authoring; existing lightmaps/probes/reflection captures can be stale. Bake into a separate output folder per setup.";
            EditorUtility.SetDirty(state); EditorSceneManager.MarkSceneDirty(state.gameObject.scene);
        }
        static void ApplySurfaces(LightingExperimentState state)
        {
            foreach (SurfaceState surface in state.surfaces)
            {
                Object target = surface.renderer != null ? (Object)surface.renderer : surface.terrain;
                if (target == null) continue;
                var so = new SerializedObject(target); var scale = so.FindProperty("m_ScaleInLightmap");
                if (scale == null) throw new InvalidOperationException("Missing Scale in Lightmap on " + target.name);
                if (!state.surfacesGated)
                {
                    surface.scale = scale.floatValue;
                    if (surface.renderer is MeshRenderer mr) { surface.receiveGI = mr.receiveGI; surface.probes = mr.lightProbeUsage; surface.lightmapIndex = mr.lightmapIndex; surface.lightmapST = mr.lightmapScaleOffset; }
                    if (surface.terrain != null) { surface.lightmapIndex = surface.terrain.lightmapIndex; surface.lightmapST = surface.terrain.lightmapScaleOffset; }
                }
                scale.floatValue = state.useLightmaps ? surface.scale : 0;
                so.ApplyModifiedProperties();
                if (surface.renderer is MeshRenderer renderer)
                {
                    renderer.receiveGI = state.useLightmaps ? surface.receiveGI : ReceiveGI.LightProbes;
                    renderer.lightProbeUsage = state.useLightmaps ? surface.probes : LightProbeUsage.Off;
                    renderer.lightmapIndex = state.useLightmaps ? surface.lightmapIndex : -1;
                    renderer.lightmapScaleOffset = surface.lightmapST;
                }
                if (surface.terrain != null) { surface.terrain.lightmapIndex = state.useLightmaps ? surface.lightmapIndex : -1; surface.terrain.lightmapScaleOffset = surface.lightmapST; }
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            }
            state.surfacesGated = !state.useLightmaps;
        }
        static void RouteMaterial(MaterialState saved, LightingExperimentState state, bool anyLV)
        {
            Material m = saved.experiment; if (m == null) throw new InvalidOperationException("An experiment material was deleted.");
            for (int i = 0; i < saved.properties.Count; i++) m.SetFloat(saved.properties[i], saved.values[i]);
            m.SetFloat("_LightVolumesToggle", anyLV ? 1 : 0);
            m.SetFloat("_AdditiveLightVolumesToggle", anyLV ? 1 : 0);
            m.SetFloat("_LightVolumeStrength", 1);
            m.SetFloat("_AdditiveLightVolumeStrength", state.lvDiffuse ? 1 : 0);
            m.SetFloat("_LightVolumeSpecularity", anyLV && state.lvSpecular ? 1 : 0);
            if (!state.useLightmaps)
            {
                // With LV strength=1 the unlightmapped Mochie path replaces Unity SH, avoiding
                // old baked probes. Diffuse-only routing on that path has no independent switch.
                if (!state.lvDiffuse) throw new InvalidOperationException("LV specular-only requires Bakery lightmaps in the installed Mochie shader. Enable Use Bakery lightmaps or LV diffuse.");
                m.SetFloat("_BAKERY_LMSPEC", 0); m.SetFloat("_BakeryLMSpecStrength", 0);
                if (!state.keepReflectionProbes) m.SetFloat("_ReflectionsToggle", 0);
            }
            EditorUtility.SetDirty(m);
        }
        internal static void BakeShadows(LightingExperimentState state)
        {
            Apply(state); new LightingExperimentAdapter().BakeShadows(state.manager);
        }
        internal static void CopyToCounterpart(LightingExperimentState state, LightState source)
        {
            Require(state);
            var target = state.lights.SingleOrDefault(l => l.component == source.counterpart);
            if (target == null || source.gated || target.gated) throw new InvalidOperationException("Enable both counterparts in Hybrid mode before explicitly copying their settings.");
            if (source.component.GetType().Name != "BakeryPointLight" && target.component.GetType().Name != "BakeryPointLight") throw new InvalidOperationException("Explicit resync currently supports point/spot counterparts. Edit area/sky approximations independently.");
            var adapter = new LightingExperimentAdapter(); int group = Begin();
            try
            {
                Record(state);
                if (source.family == Family.Bakery)
                {
                    LightingExperimentConversion.CopyToVolume(source.component, target.component, false, new Bounds(), 1);
                    adapter.Sync(target.component);
                }
                else LightingExperimentConversion.CopyToBakery(source.component, target.component, false, 1);
                CaptureLight(target, true); ApplyCore(state, adapter); Undo.CollapseUndoOperations(group);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }
        internal static List<Component> CleanupTargets(LightingExperimentState state, bool removeBakery)
        {
            Require(state);
            return Components(state.gameObject.scene).Where(c => removeBakery ? IsBakerySceneComponent(c) : c.GetType().Namespace == "VRCLightVolumes" && new[] { "PointLightVolumeInstance", "LightVolumeInstance", "LightVolumeManager", "PointLightShadowRuntimeBaker", "LightVolumeTVGI", "LightVolumeAudioLink" }.Contains(c.GetType().Name)).ToList();
        }
        static bool IsBakerySceneComponent(Component c) => c.GetType().Assembly.GetName().Name == "BakeryRuntimeAssembly" && (c.GetType().Name.StartsWith("Bakery", StringComparison.Ordinal) || c.GetType().Name == "ftLightmapsStorage");
        internal static void Finalize(LightingExperimentState state, bool removeBakery)
        {
            Require(state); var targets = CleanupTargets(state, removeBakery);
            int group = Begin();
            try
            {
                Record(state);
                if (removeBakery)
                {
                    state.bakeryLights = false; state.useLightmaps = false; state.lvDiffuse = true;
                    if (!state.pointLights && !state.bakedVolumes) state.pointLights = true;
                    state.mode = Mode.LightVolumes;
                }
                else SetModeFields(state, Mode.Bakery);
                ApplyCore(state, new LightingExperimentAdapter());
                foreach (Component target in targets)
                {
                    if (target == null) continue;
                    var entry = state.lights.FirstOrDefault(l => l.component == target);
                    bool ownsObject = entry?.generatedObject == target.gameObject || state.generatedManager == target.gameObject;
                    GameObject owned = ownsObject ? target.gameObject : null;
                    if (!removeBakery && target.GetType().Name == "LightVolumeInstance")
                    {
                        Component helper = Get(target, "BakeryVolume") as Component;
                        if (helper != null && helper.transform.parent == target.transform) Undo.DestroyObjectImmediate(helper);
                    }
                    // Native LV components can own Udon backings and Bakery helpers. Use the
                    // inspected UdonSharp API when removing a proxy, retaining unrelated content.
                    RemoveComponent(target);
                    if (owned != null && owned.transform.childCount == 0 && owned.GetComponents<Component>().All(c => c is Transform || (entry?.family == Family.Bakery && (c is MeshRenderer || c is MeshFilter)))) Undo.DestroyObjectImmediate(owned);
                }
                if (removeBakery && state.manager != null) { Set(state.manager, "BakingMode", 0); new LightingExperimentAdapter().Sync(state.manager); }
                if (!removeBakery)
                {
                    foreach (var assignment in state.assignments.Where(a => a.renderer != null))
                    {
                        Undo.RecordObject(assignment.renderer, Operation);
                        Material[] current = assignment.renderer.sharedMaterials;
                        for (int i = 0; i < current.Length; i++)
                        {
                            var saved = state.materials.FirstOrDefault(m => m.experiment == current[i]);
                            // Keep experiment material edits; only restore original routing values.
                            if (saved != null) for (int j = 0; j < saved.properties.Count; j++) saved.experiment.SetFloat(saved.properties[j], saved.values[j]);
                        }
                    }
                    Shader.SetGlobalFloat("_UdonLightVolumeEnabled", 0);
                }
                state.finished = true; state.status = "Finalized. Re-bake the retained setup. Source packages and texture assets were retained.";
                EditorUtility.SetDirty(state); Undo.CollapseUndoOperations(group);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }
        static void RemoveComponent(Component component)
        {
            Type undo = Find("UdonSharpEditor.UdonSharpUndo");
            var method = undo?.GetMethods().FirstOrDefault(m => m.Name == "DestroyImmediate" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(component));
            if (component.GetType().Namespace == "VRCLightVolumes" && method != null) method.Invoke(null, new object[] { component });
            else Undo.DestroyObjectImmediate(component);
        }
    }
}
