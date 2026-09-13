using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.WorldTools
{
    internal sealed class MochiePackedMapDuplicatesWindow : EditorWindow
    {
        private MochiePackedMapDuplicates.Preview preview;
        private Vector2 scroll;
        private string message;
        private bool notes;

        [MenuItem("Tools/Lightbulb/Consolidate Mochie Packed Maps in Scene")]
        internal static void Open()
        {
            var window = GetWindow<MochiePackedMapDuplicatesWindow>("Consolidate Packed Maps");
            window.minSize = new Vector2(580, 400);
            window.Show();
        }

        private void OnEnable() { Undo.undoRedoPerformed += Invalidate; }
        private void OnDisable() { Undo.undoRedoPerformed -= Invalidate; }
        private void Invalidate() { preview = null; message = "Materials changed. Scan again."; Repaint(); }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Consolidate Mochie Packed Maps in Scene", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Finds byte-identical packed PNGs with identical texture import settings on Standard / Standard Lite materials " +
                "in the active scene, including inactive objects. Each selected group will share the displayed keeper texture.", MessageType.Info);
            EditorGUILayout.HelpBox("Materials stay separate, preserving AreaLit offsets and all other settings. No files are deleted. " +
                "Shared material assets change in their other uses too. Undo restores the original texture references.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!MaterialTextureBatch.IsIdle))
            {
                if (GUILayout.Button("Scan active scene for duplicates")) Scan();
                if (preview != null)
                {
                    EditorGUILayout.LabelField($"{preview.Scene.name} | {preview.Groups.Count} duplicate groups");
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Select all")) foreach (var group in preview.Groups) group.Included = true;
                        if (GUILayout.Button("Select none")) foreach (var group in preview.Groups) group.Included = false;
                    }
                    using (var view = new EditorGUILayout.ScrollViewScope(scroll))
                    {
                        scroll = view.scrollPosition;
                        foreach (var group in preview.Groups)
                            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                            {
                                group.Included = EditorGUILayout.ToggleLeft($"Share one texture across {group.Replacements.Select(s => s.Material).Distinct().Count()} affected material(s)", group.Included);
                                EditorGUILayout.ObjectField("Keep", group.Keep, typeof(Texture2D), false);
                                EditorGUILayout.LabelField(AssetDatabase.GetAssetPath(group.Keep), EditorStyles.wordWrappedMiniLabel);
                                foreach (var texture in group.Replacements.Select(s => s.Texture).Distinct())
                                {
                                    EditorGUILayout.ObjectField("Replace reference to", texture, typeof(Texture2D), false);
                                    EditorGUILayout.LabelField(AssetDatabase.GetAssetPath(texture), EditorStyles.wordWrappedMiniLabel);
                                }
                                foreach (var slot in group.Replacements)
                                    EditorGUILayout.LabelField(AssetDatabase.GetAssetPath(slot.Material) + " : " + slot.Property, EditorStyles.wordWrappedMiniLabel);
                            }
                        notes = EditorGUILayout.Foldout(notes, $"Skipped / scan notes ({preview.Notes.Count})", true);
                        if (notes) foreach (string note in preview.Notes) EditorGUILayout.LabelField(note, EditorStyles.wordWrappedLabel);
                    }
                    int count = preview.Groups.Count(g => g.Included);
                    using (new EditorGUI.DisabledScope(count == 0))
                        if (GUILayout.Button($"Consolidate selected groups ({count})")) Apply();
                }
                if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
            }
        }

        private void Scan()
        {
            preview = null;
            try
            {
                preview = MochiePackedMapDuplicates.Collect(SceneMaterials.Active(), name =>
                    EditorUtility.DisplayCancelableProgressBar("Comparing packed maps", name, 0.5f));
                message = preview.Groups.Count == 0 ? "No exact duplicate packed maps found." : null;
            }
            catch (OperationCanceledException) { message = "Scan cancelled. No materials changed."; }
            catch (Exception ex) { message = ex.GetBaseException().Message; }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private void Apply()
        {
            try { SceneMaterials.RequireActive(preview.Scene); }
            catch (Exception ex) { message = ex.Message; return; }
            if (!EditorUtility.DisplayDialog("Consolidate Mochie Packed Maps",
                $"Consolidate {preview.Groups.Count(g => g.Included)} selected group(s) in '{preview.Scene.name}'?\n\n" +
                "Only packed texture references change. Shared materials change in their other uses too. " +
                "All texture files remain on disk. Undo restores the references. Review before saving.", "Consolidate", "Cancel")) return;
            try
            {
                int changed = MochiePackedMapDuplicates.Apply(preview);
                message = $"Updated {changed} packed-map references. No files deleted. Review the scene, then save. Undo restores references.";
                Debug.Log("[Lightbulb] " + message);
                preview = null;
            }
            catch (Exception ex) { message = ex.GetBaseException().Message; Debug.LogException(ex); }
        }
    }
}
