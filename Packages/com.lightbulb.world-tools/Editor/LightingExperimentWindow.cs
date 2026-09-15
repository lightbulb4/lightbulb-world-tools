using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using static Lightbulb.WorldTools.LightingExperimentState;

namespace Lightbulb.WorldTools
{
    internal sealed class LightingExperimentWindow : EditorWindow
    {
        [SerializeField] bool fromBakery = true, forcePoint, addVolumes = true, acknowledge;
        [SerializeField] int maxVolumes = 10, geometryLayers = ~0;
        [SerializeField] float padding = .5f, density = 1, brightness = 1;
        [SerializeField] bool showLights = true, showConversion;
        Vector2 scroll;
        LightingExperiment.Preview preview;
        string error;
        [MenuItem("Tools/Lightbulb/Bakery LV3 Swapper")]
        internal static void Open() => GetWindow<LightingExperimentWindow>("Bakery LV3 Swapper").Show();
        void OnEnable() { minSize = new Vector2(640, 480); Undo.undoRedoPerformed += Changed; SceneView.duringSceneGui += DrawBounds; }
        void OnDisable() { Undo.undoRedoPerformed -= Changed; SceneView.duringSceneGui -= DrawBounds; }
        void Changed() { preview = null; Repaint(); }
        void DrawBounds(SceneView view)
        {
            if (preview == null || !addVolumes) return;
            Handles.color = new Color(.2f, 1, .8f, .8f);
            foreach (Bounds b in preview.volumes) Handles.DrawWireCube(b.center, b.size);
        }
        void Run(Action action) { try { error = null; action(); } catch (Exception e) { error = e.Message; Debug.LogException(e); } Repaint(); }
        void OnGUI()
        {
            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                EditorGUILayout.LabelField("Bakery LV3 Swapper", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("Keep both setups in this scene. Conversion only creates missing counterparts. Mode switches preserve independent edits; rebake after changing lighting. Save the scene to preserve the experiment across restarts.", MessageType.Info);
                if (GUILayout.Button("Clean up tool files…")) ToolFileCleanupWindow.Open();
                if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
                LightingExperimentState state = null;
                try { state = LightingExperiment.FindState(UnityEngine.SceneManagement.SceneManager.GetActiveScene()); }
                catch (Exception e) { EditorGUILayout.HelpBox(e.Message, MessageType.Error); return; }
                using (new EditorGUI.DisabledScope(!MaterialTextureBatch.IsIdle))
                {
                    if (state == null) DrawConversion(null);
                    else if (state.finished) EditorGUILayout.HelpBox(state.status + " Undo finalization to return to the experiment.", MessageType.Info);
                    else
                    {
                        DrawModes(state);
                        EditorGUILayout.HelpBox(state.status, MessageType.Warning);
                        EditorGUILayout.LabelField("Last preset", state.mode.ToString());
                        if (!string.IsNullOrEmpty(state.backupScene) && GUILayout.Button("Select pre-swap scene backup")) Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(state.backupScene);
                        DrawRouting(state);
                        showLights = EditorGUILayout.Foldout(showLights, "Lights and inclusion", true);
                        if (showLights) DrawLights(state);
                        showConversion = EditorGUILayout.Foldout(showConversion, "Create missing counterparts", true);
                        if (showConversion) DrawConversion(state);
                        GUILayout.Space(10);
                        EditorGUILayout.LabelField("Bake", EditorStyles.boldLabel);
                        EditorGUILayout.HelpBox("Known native bake issue: Light Volumes dev.18 can report a RenderTexture format error on Mochie surfaces. This also occurs outside experiments; check the Console after baking.", MessageType.Warning);
                        if (GUILayout.Button("Open Bakery bake window")) Run(() => { LightingExperiment.Apply(state); if (!EditorApplication.ExecuteMenuItem("Bakery/Render lightmap...") && !EditorApplication.ExecuteMenuItem("Tools/Bakery/Render lightmap...")) throw new InvalidOperationException("Bakery bake window is unavailable."); });
                        if (GUILayout.Button("Bake enabled Point Light Volume shadows")) Run(() => LightingExperiment.BakeShadows(state));
                        if (GUILayout.Button("Select Light Volume Manager")) Selection.activeObject = state.manager;
                        GUILayout.Space(10);
                        EditorGUILayout.LabelField("Finish setup", EditorStyles.boldLabel);
                        EditorGUILayout.HelpBox("Cleanup removes lighting components from this scene, including their Udon backings. Meshes, source packages, materials and baked texture files remain. Review the listed targets before confirming.", MessageType.Info);
                        if (GUILayout.Button("Remove Bakery components; keep Light Volumes…")) Cleanup(state, true);
                        if (GUILayout.Button("Remove Light Volumes; keep Bakery…")) Cleanup(state, false);
                    }
                }
            }
        }
        void DrawModes(LightingExperimentState state)
        {
            using (new EditorGUILayout.HorizontalScope())
                foreach (Mode mode in Enum.GetValues(typeof(Mode)))
                    if (GUILayout.Button(mode == Mode.LightVolumes ? "Point LVs only" : mode.ToString())) Run(() => LightingExperiment.Switch(state, mode));
        }
        void DrawRouting(LightingExperimentState state)
        {
            EditorGUILayout.LabelField("Custom combination", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Routing is verified for Mochie Standard / Standard Lite. The tool uses scene-assigned material copies so other scenes are unaffected. Gates temporarily zero excluded light intensity and bake flags; their authoring values are stored below.", MessageType.Info);
            Undo.RecordObject(state, "Edit lighting options");
            state.bakeryLights = EditorGUILayout.Toggle("Bakery lights + Unity lights", state.bakeryLights);
            state.pointLights = EditorGUILayout.Toggle("Point / spot / area LVs", state.pointLights);
            state.bakedVolumes = EditorGUILayout.Toggle("Regular baked volumes", state.bakedVolumes);
            state.useLightmaps = EditorGUILayout.Toggle("Use Bakery lightmaps", state.useLightmaps);
            state.lvDiffuse = EditorGUILayout.Toggle("LV diffuse lighting", state.lvDiffuse);
            state.lvSpecular = EditorGUILayout.Toggle("LV specular highlights", state.lvSpecular);
            state.lvShadows = EditorGUILayout.Toggle("LV shadows", state.lvShadows);
            state.keepReflectionProbes = EditorGUILayout.Toggle("Keep reflection probes in LV mode", state.keepReflectionProbes);
            if (GUILayout.Button("Apply combination / inclusion changes")) Run(() => LightingExperiment.Apply(state));
        }
        void DrawLights(LightingExperimentState state)
        {
            foreach (LightState light in state.lights)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        light.included = EditorGUILayout.Toggle(light.included, GUILayout.Width(20));
                        EditorGUILayout.ObjectField(light.component, typeof(Component), true);
                        GUILayout.Label(light.family.ToString(), GUILayout.Width(95));
                    }
                    if (light.gated)
                    {
                        light.enabled = EditorGUILayout.Toggle("Saved enabled state", light.enabled);
                        if (light.family == Family.BakedVolume) light.activeSelf = EditorGUILayout.Toggle("Saved object active state", light.activeSelf);
                        light.intensity = EditorGUILayout.FloatField("Saved intensity", light.intensity);
                        if (light.family == Family.PointVolume)
                        {
                            light.shadows = EditorGUILayout.Toggle("Saved shadows", light.shadows);
                            light.rebakeShadows = EditorGUILayout.Toggle("Saved rebake shadows", light.rebakeShadows);
                        }
                    }
                    if (light.counterpart != null)
                    {
                        EditorGUILayout.ObjectField("Counterpart", light.counterpart, typeof(Component), true);
                        if (GUILayout.Button("Copy settings to counterpart…")) Run(() => { if (EditorUtility.DisplayDialog("Overwrite counterpart settings?", "This explicitly copies supported settings. Ordinary mode switches never do this. Both counterparts must be enabled in Hybrid mode.", "Copy settings", "Cancel")) LightingExperiment.CopyToCounterpart(state, light); });
                    }
                }
            }
        }
        void DrawConversion(LightingExperimentState state)
        {
            EditorGUI.BeginChangeCheck();
            fromBakery = EditorGUILayout.Toggle("Convert from Bakery", fromBakery);
            forcePoint = EditorGUILayout.Toggle("Force all converted lights to points", forcePoint);
            brightness = EditorGUILayout.FloatField("Conversion brightness multiplier", brightness);
            if (state == null)
            {
                addVolumes = EditorGUILayout.Toggle("Create fitted regular volumes", addVolumes);
                maxVolumes = EditorGUILayout.IntSlider("Maximum volumes", maxVolumes, 1, 10);
                padding = EditorGUILayout.FloatField("Bounds padding (meters)", padding);
                density = EditorGUILayout.FloatField("Voxels per meter", density);
                geometryLayers = EditorGUILayout.MaskField("Geometry layers", geometryLayers, Enumerable.Range(0, 32).Select(i => string.IsNullOrEmpty(LayerMask.LayerToName(i)) ? "Layer " + i : LayerMask.LayerToName(i)).ToArray());
            }
            if (EditorGUI.EndChangeCheck()) { preview = null; acknowledge = false; }
            if (GUILayout.Button("Preview active scene")) Run(() => { preview = LightingExperiment.Scan(fromBakery, forcePoint, maxVolumes, padding, density, geometryLayers); preview.brightness = brightness; SceneView.RepaintAll(); });
            if (preview == null) return;
            EditorGUILayout.LabelField(preview.candidates.Count + " unpaired lights; " + preview.volumes.Count + " proposed volumes");
            double voxels = preview.volumes.Sum(b => Math.Ceiling(b.size.x * density) * Math.Ceiling(b.size.y * density) * Math.Ceiling(b.size.z * density));
            EditorGUILayout.LabelField("Raw SH estimate (3 × RGBAHalf)", (voxels * 24 / 1048576).ToString("F1") + " MiB, before atlas padding");
            foreach (var c in preview.candidates)
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(c.problem != null)) c.included = EditorGUILayout.Toggle(c.included, GUILayout.Width(20));
                        EditorGUILayout.ObjectField(c.source, typeof(Component), true);
                    }
                    EditorGUILayout.LabelField(c.problem ?? c.description, EditorStyles.wordWrappedLabel);
                }
            foreach (string warning in preview.warnings) EditorGUILayout.HelpBox(warning, MessageType.Warning);
            acknowledge = EditorGUILayout.ToggleLeft("I reviewed approximations, unsupported shaders and the bake requirement", acknowledge);
            using (new EditorGUI.DisabledScope(!acknowledge))
                if (GUILayout.Button(state == null ? "Create swapper setup and selected counterparts" : "Create selected missing counterparts")) Run(() => { if (state == null) LightingExperiment.Create(preview, addVolumes); else LightingExperiment.AddCounterparts(preview, state); preview = null; });
        }
        void Cleanup(LightingExperimentState state, bool bakery)
        {
            Run(() =>
            {
                var targets = LightingExperiment.CleanupTargets(state, bakery);
                string names = string.Join("\n", targets.Take(25).Select(c => c.name + " — " + c.GetType().Name));
                if (targets.Count > 25) names += "\n… and " + (targets.Count - 25) + " more";
                if (EditorUtility.DisplayDialog("Remove " + targets.Count + " lighting components?", names + "\n\nThe remaining setup requires a bake. Undo is available until Unity's undo history is cleared. No texture assets or packages are deleted.", "Remove components", "Cancel")) LightingExperiment.Finalize(state, bakery);
            });
        }
    }
}
