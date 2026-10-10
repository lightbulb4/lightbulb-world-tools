using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.WorldTools
{
    internal sealed class MochieLightVolumesWindow : EditorWindow
    {
        private static readonly string[] ToggleOptions = { "Keep", "On", "Off" };
        private static readonly string[] NumberOptions = { "Keep", "Set", "Add" };
        [SerializeField] private int[] operations = new int[7];
        [SerializeField] private float[] values = { 0, 1, 0, 1, 0, 1, 0 };
        private Material[] materials;
        private string sceneName, message;
        private bool showMaterials, error;
        private Vector2 scroll;

        [MenuItem("Tools/Lightbulb/Mochie Light Volumes in Scene")]
        internal static void Open()
        {
            var window = GetWindow<MochieLightVolumesWindow>("Mochie Light Volumes");
            window.minSize = new Vector2(520, 560);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += Invalidate;
            EditorApplication.projectChanged += Invalidate;
            Undo.undoRedoPerformed += Invalidate;
        }
        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= Invalidate;
            EditorApplication.projectChanged -= Invalidate;
            Undo.undoRedoPerformed -= Invalidate;
        }
        private void Invalidate() { materials = null; Repaint(); }
        private void OnInspectorUpdate()
        {
            if (!SceneMaterials.IsLightingIdle) return;
            if (materials == null) Execute(Scan);
            Repaint();
        }

        private void OnGUI()
        {
            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                EditorGUILayout.LabelField("Mochie Light Volumes in Scene", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Edit Standard and Standard Lite materials in the active scene, including inactive objects and skinned renderers.", EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("Changes are saved to shared material assets and affect every scene or prefab using them. Normal Unity Undo is available.", MessageType.Info);
                using (new EditorGUI.DisabledScope(!SceneMaterials.IsLightingIdle))
                {
                    if (GUILayout.Button("Scan active scene")) Execute(Scan);
                    if (materials != null)
                    {
                        EditorGUILayout.LabelField($"{sceneName}: {materials.Length} matching materials", EditorStyles.boldLabel);
                        if (materials.Length == 0)
                            EditorGUILayout.HelpBox("No Mochie/Standard or Mochie/Standard Lite materials are assigned to this scene.", MessageType.Info);
                        showMaterials = EditorGUILayout.Foldout(showMaterials, "Materials", true);
                        if (showMaterials)
                            using (new EditorGUI.DisabledScope(true))
                                foreach (Material material in materials) EditorGUILayout.ObjectField(material, typeof(Material), false);
                    }
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Settings to apply", EditorStyles.boldLabel);
                    for (int i = 0; i < MochieLightVolumes.Settings.Length; i++)
                    {
                        var setting = MochieLightVolumes.Settings[i];
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField(setting.Label, GUILayout.MinWidth(170));
                            operations[i] = EditorGUILayout.Popup(operations[i], setting.Toggle ? ToggleOptions : NumberOptions, GUILayout.Width(80));
                            using (new EditorGUI.DisabledScope(operations[i] == 0 || setting.Toggle))
                            {
                                if (setting.Toggle) GUILayout.Space(110);
                                else values[i] = EditorGUILayout.FloatField(values[i], GUILayout.Width(110));
                            }
                        }
                        if (materials != null)
                            EditorGUILayout.LabelField(MochieLightVolumes.Counts(materials, i), EditorStyles.wordWrappedMiniLabel);
                    }
                    EditorGUILayout.LabelField("Set replaces each value. Add offsets each material's current value. Keep leaves it alone. Strength is limited to 0–1; other numbers match Mochie's unrestricted fields.", EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.Space();
                    using (new EditorGUI.DisabledScope(!operations.Any(o => o != 0)))
                        if (GUILayout.Button("Apply settings to active scene", GUILayout.Height(28))) Execute(() => Apply(Edits()));
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Adjust specular strength only", EditorStyles.boldLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("−1 to every material")) Execute(() => Step(-1));
                        if (GUILayout.Button("+1 to every material")) Execute(() => Step(1));
                    }
                    EditorGUILayout.LabelField("These buttons apply immediately and leave all other settings, including the specular toggle, unchanged.", EditorStyles.wordWrappedMiniLabel);
                }
                if (!SceneMaterials.IsLightingIdle) EditorGUILayout.HelpBox("Wait until Unity is idle, outside Play Mode, and finished baking.", MessageType.Info);
                if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, error ? MessageType.Error : MessageType.Info);
            }
        }

        private MochieLightVolumes.Edit[] Edits() => MochieLightVolumes.Settings.Select((s, i) =>
            new MochieLightVolumes.Edit(i, s.Toggle ? (operations[i] == 0 ? MochieLightVolumes.Operation.Keep : MochieLightVolumes.Operation.Set) :
                (MochieLightVolumes.Operation)operations[i], s.Toggle ? (operations[i] == 1 ? 1 : 0) : values[i])).ToArray();

        private void Scan()
        {
            var scene = SceneMaterials.Active();
            materials = MochieLightVolumes.Collect(scene).ToArray();
            sceneName = scene.name;
        }
        private void Apply(MochieLightVolumes.Edit[] edits)
        {
            int count = MochieLightVolumes.Apply(SceneMaterials.Active(), edits);
            Scan();
            message = $"Saved changes to {count} material(s) in {sceneName}.";
        }
        private void Step(float amount) => Apply(new[] { new MochieLightVolumes.Edit(MochieLightVolumes.SpecularStrength, MochieLightVolumes.Operation.Add, amount) });
        private void Execute(Action action)
        {
            error = false; message = null;
            try { action(); }
            catch (Exception ex) { error = true; message = ex.GetBaseException().Message; }
        }
    }
}
