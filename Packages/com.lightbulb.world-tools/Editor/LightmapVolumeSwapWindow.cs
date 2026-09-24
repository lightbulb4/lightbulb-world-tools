using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.WorldTools
{
    internal sealed class LightmapVolumeSwapWindow : EditorWindow
    {
        [SerializeField] bool changeScale = true, changeVolumes = true, changeSpecularity = true;
        Vector2 scroll;
        LightmapVolumeSwap.Preview preview;
        string error, status;
        [MenuItem("Tools/Lightbulb/Bakery LV3 Swapper")]
        internal static void Open() => GetWindow<LightmapVolumeSwapWindow>("Bakery LV3 Swapper").Show();
        void OnEnable() { minSize = new Vector2(520, 440); Undo.undoRedoPerformed += Changed; EditorApplication.hierarchyChanged += Changed; }
        void OnDisable() { Undo.undoRedoPerformed -= Changed; EditorApplication.hierarchyChanged -= Changed; }
        void Changed() { preview = null; Repaint(); }
        void Run(Action action) { try { error = null; action(); } catch (Exception e) { error = e.Message; } Repaint(); }
        void OnGUI()
        {
            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                EditorGUILayout.LabelField("Bakery LV3 Swapper", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("Scans the active scene, including inactive objects. Targets Mesh Renderers that contribute GI and use Mochie/Standard. Contribute GI stays enabled.", MessageType.Info);
                EditorGUILayout.HelpBox("Rebake after changing Scale in Lightmap in either direction. This tool does not clear or reassign existing lightmaps. Save the scene and materials to preserve your settings and restore record.", MessageType.Info);
                if (error != null) EditorGUILayout.HelpBox(error, MessageType.Error);
                if (status != null) EditorGUILayout.HelpBox(status, MessageType.Info);
                var scene = SceneManager.GetActiveScene();
                LightmapVolumeSwapState state = null;
                string blocked = null;
                try { LightmapVolumeSwap.RequireReady(scene); state = LightmapVolumeSwap.State(scene); }
                catch (Exception e) { blocked = e.Message; }
                if (blocked != null) EditorGUILayout.HelpBox(blocked, MessageType.Warning);
                using (new EditorGUI.DisabledScope(blocked != null))
                {
                    if (state != null && state.applied)
                    {
                        EditorGUILayout.LabelField("Recorded swap", state.surfaces.Count + " renderers / " + state.materials.Count + " materials");
                        EditorGUILayout.HelpBox("Restore writes the original values for the options changed by this tool, including originally enabled toggles and individual scales. Later edits to those same values will be replaced.", MessageType.Info);
                        if (GUILayout.Button("Reverse: restore original settings")) Run(() => { LightmapVolumeSwap.Restore(state); preview = null; status = "Original settings restored. Rebake if Scale in Lightmap changed."; });
                        return;
                    }
                    changeScale = EditorGUILayout.Toggle("Set Scale in Lightmap to 0", changeScale);
                    changeVolumes = EditorGUILayout.Toggle("Enable Light Volumes", changeVolumes);
                    changeSpecularity = EditorGUILayout.Toggle("Enable Light Volume specularity", changeSpecularity);
                    if (GUILayout.Button("Scan active scene")) Run(() => { preview = LightmapVolumeSwap.Scan(scene); status = null; });
                    if (preview == null || preview.scene != scene) return;
                    EditorGUILayout.LabelField(preview.renderers.Length + " renderers / " + preview.materials.Length + " unique materials", EditorStyles.boldLabel);
                    if (changeVolumes || changeSpecularity)
                        EditorGUILayout.HelpBox("Material changes affect every user of those shared materials, including other scenes and prefabs. Additional loaded renderer users: " + preview.otherMaterialUsers + ".", MessageType.Warning);
                    if (changeScale && preview.mixedRenderers > 0)
                        EditorGUILayout.HelpBox(preview.mixedRenderers + " renderers also use other shaders. Scale in Lightmap affects the whole renderer, including those slots.", MessageType.Warning);
                    using (new EditorGUI.DisabledScope(preview.renderers.Length == 0 || !(changeScale || changeVolumes || changeSpecularity)))
                        if (GUILayout.Button("Apply selected changes")) Run(() => { LightmapVolumeSwap.Apply(preview, changeScale, changeVolumes, changeSpecularity); preview = null; status = "Changes applied. Save the scene and materials; rebake if Scale in Lightmap changed."; });
                    if (preview == null) return;
                    foreach (var r in preview.renderers)
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.ObjectField(r, typeof(MeshRenderer), true);
                            GUILayout.Label("Scale " + LightmapVolumeSwap.Scale(r).ToString("0.###"), GUILayout.Width(85));
                        }
                    EditorGUILayout.LabelField("Shared materials", EditorStyles.boldLabel);
                    foreach (var m in preview.materials) EditorGUILayout.ObjectField(m, typeof(Material), false);
                }
            }
        }
    }
}
