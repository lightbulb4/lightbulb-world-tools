using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.WorldTools
{
    internal sealed class MaterialTextureBatchWindow : EditorWindow
    {
        private const string ContextMenu = "Assets/Materials/Resize Referenced Textures...";
        private const string ToolsMenu = "Tools/Lightbulb/Resize Referenced Textures...";
        private static readonly int[] Sizes = { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384 };
        private static readonly string[] SizeLabels = Sizes.Select(s => s.ToString()).ToArray();
        private static readonly string[] CrunchLabels = { "Leave unchanged", "Enable", "Disable" };
        [SerializeField] private Material[] materials = Array.Empty<Material>();
        [SerializeField] private int maximum = 1024;
        [SerializeField] private MaterialTextureBatch.CrunchMode crunch;
        private List<MaterialTextureBatch.Entry> entries = new List<MaterialTextureBatch.Entry>();
        private Vector2 scroll;
        private string message;

        [MenuItem(ContextMenu)]
        [MenuItem(ToolsMenu)]
        private static void Open()
        {
            var window = GetWindow<MaterialTextureBatchWindow>("Material Textures");
            window.minSize = new Vector2(570, 420);
            window.materials = SelectedMaterials();
            window.RefreshPreview();
            window.Show();
        }

        [MenuItem(ContextMenu, true)]
        [MenuItem(ToolsMenu, true)]
        private static bool ValidateMenu() => MaterialTextureBatch.IsIdle && SelectedMaterials().Length > 0;

        private static Material[] SelectedMaterials() => Selection.objects.OfType<Material>().Distinct().ToArray();

        private void OnEnable() { RefreshPreview(); }

        private void RefreshPreview()
        {
            var excluded = new HashSet<string>(entries.Where(e => !e.Included && e.Changes.Count > 0).Select(e => e.Path));
            try
            {
                entries = MaterialTextureBatch.Collect(materials, maximum, crunch);
                foreach (var entry in entries) if (excluded.Contains(entry.Path)) entry.Included = false;
                message = null;
            }
            catch (Exception ex) { entries.Clear(); message = ex.Message; }
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Resize Referenced Textures", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Changes affect EVERY material and scene sharing these textures. " +
                "Original images are untouched; import settings are backed up before applying. This is not Unity Undo.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!MaterialTextureBatch.IsIdle))
            {
                EditorGUI.BeginChangeCheck();
                maximum = EditorGUILayout.IntPopup("Maximum resolution", maximum, SizeLabels, Sizes);
                crunch = (MaterialTextureBatch.CrunchMode)EditorGUILayout.Popup("Crunch compression", (int)crunch, CrunchLabels);
                if (EditorGUI.EndChangeCheck()) RefreshPreview();
                EditorGUILayout.LabelField("Applies to Default and all existing enabled platform overrides.", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField("Size only decreases. Crunch applies independently, even to smaller textures. " +
                    "Automatic follows Unity's format selection; incompatible explicit formats are skipped.", EditorStyles.wordWrappedMiniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Use selected materials"))
                    {
                        materials = SelectedMaterials();
                        entries.Clear();
                        RefreshPreview();
                    }
                    if (GUILayout.Button("Refresh preview")) RefreshPreview();
                    if (GUILayout.Button("Select all")) foreach (var entry in entries) entry.Included = entry.Changes.Count > 0;
                    if (GUILayout.Button("Select none")) foreach (var entry in entries) entry.Included = false;
                }
                int count = entries.Count(e => e.Included && e.Changes.Count > 0);
                EditorGUILayout.LabelField($"{materials.Count(m => m != null)} materials | {entries.Count} unique textures | {count} selected changes");
                scroll = EditorGUILayout.BeginScrollView(scroll);
                foreach (var entry in entries)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            using (new EditorGUI.DisabledScope(entry.Changes.Count == 0))
                                entry.Included = EditorGUILayout.Toggle(entry.Included, GUILayout.Width(18));
                            EditorGUILayout.ObjectField(entry.Texture, typeof(Texture), false);
                        }
                        EditorGUILayout.LabelField(entry.Path, EditorStyles.wordWrappedMiniLabel);
                        EditorGUILayout.LabelField(string.Join("\n", entry.Notes), EditorStyles.wordWrappedLabel);
                        EditorGUILayout.LabelField(new GUIContent($"{entry.Uses.Count} selected material slot(s)",
                            string.Join("\n", entry.Uses)), EditorStyles.miniLabel);
                    }
                }
                EditorGUILayout.EndScrollView();
                if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
                using (new EditorGUI.DisabledScope(count == 0))
                    if (GUILayout.Button($"Apply to {count} texture(s)")) Apply();
            }
        }

        private void Apply()
        {
            int count = entries.Count(e => e.Included && e.Changes.Count > 0);
            if (!EditorUtility.DisplayDialog("Update Material Textures",
                $"Update and reimport {count} texture(s)? Shared textures change everywhere.\n\n" +
                "The first original .meta file per texture is retained under Library/LightbulbWorldTools. " +
                "This operation does not use Unity Undo.", "Apply", "Cancel")) return;
            try
            {
                var result = MaterialTextureBatch.Apply(entries, (index, total, path) =>
                    EditorUtility.DisplayCancelableProgressBar("Updating material textures", path, (float)index / total));
                RefreshPreview();
                message = $"{result.Changed} changed; {result.Failed} failed." +
                    (result.Cancelled ? " Cancelled; completed changes remain applied." : "") +
                    " See Console for details and backup location.";
            }
            catch (Exception ex)
            {
                message = ex.Message;
                Debug.LogError("[Lightbulb] Texture batch stopped: " + ex.Message);
            }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
}
