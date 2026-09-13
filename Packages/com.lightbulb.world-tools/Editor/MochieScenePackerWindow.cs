using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.WorldTools
{
    internal sealed class MochieScenePackerWindow : EditorWindow
    {
        [SerializeField] private bool includeDetail = true;
        private MochieScenePacker.Preview preview;
        private MochieScenePacker.Adapter adapter;
        private Vector2 scroll;
        private string message;
        private bool notes;

        [MenuItem("Tools/Lightbulb/Pack Mochie Materials in Scene")]
        internal static void Open()
        {
            var window = GetWindow<MochieScenePackerWindow>("Pack Mochie Materials");
            window.minSize = new Vector2(580, 400);
            window.Show();
        }

        private void OnEnable() { Undo.undoRedoPerformed += Invalidate; }
        private void OnDisable() { Undo.undoRedoPerformed -= Invalidate; }
        private void Invalidate() { preview = null; message = "Materials changed. Scan again."; Repaint(); }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Pack Mochie Materials in Scene", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Uses Mochie Standard v2.13's installed texture packer. Scans materials assigned to the active scene, " +
                "including inactive objects. Supports Standard and Standard Lite primary maps, and Standard detail maps.", MessageType.Info);
            EditorGUILayout.HelpBox("Successful packing clears the separate data-map references. Already-packed materials with leftover references " +
                "are included for cleanup without repacking. Texture files are kept; Undo restores the references.", MessageType.Info);
            EditorGUILayout.HelpBox("Matching packing inputs share one new PNG per batch, saved beside the first matching material. AreaLit settings stay independent. " +
                "Shared material assets also change wherever else they are used. " +
                "Undo restores material settings; generated PNGs remain on disk. Review the scene before saving.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!MaterialTextureBatch.IsIdle))
            {
                EditorGUI.BeginChangeCheck();
                includeDetail = EditorGUILayout.Toggle("Include Standard detail maps", includeDetail);
                if (EditorGUI.EndChangeCheck()) preview = null;
                if (GUILayout.Button("Scan active scene")) Scan();
                if (GUILayout.Button("Find existing duplicate packed maps...")) MochiePackedMapDuplicatesWindow.Open();
                if (preview != null)
                {
                    EditorGUILayout.LabelField($"{preview.Scene.name} | {preview.Entries.Count} eligible materials | {preview.Notes.Count} notes");
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Select all")) foreach (var entry in preview.Entries) entry.Included = true;
                        if (GUILayout.Button("Select none")) foreach (var entry in preview.Entries) entry.Included = false;
                    }
                    using (var view = new EditorGUILayout.ScrollViewScope(scroll))
                    {
                        scroll = view.scrollPosition;
                        foreach (var entry in preview.Entries)
                            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                            {
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    entry.Included = EditorGUILayout.Toggle(entry.Included, GUILayout.Width(18));
                                    EditorGUILayout.ObjectField(entry.Material, typeof(Material), false);
                                }
                                if (entry.Primary) EditorGUILayout.LabelField("Pack primary: AO / roughness / metallic / height", EditorStyles.wordWrappedMiniLabel);
                                if (entry.Detail) EditorGUILayout.LabelField("Pack detail: AO / roughness / metallic", EditorStyles.wordWrappedMiniLabel);
                                if (entry.CleanupPrimary) EditorGUILayout.LabelField("Clear leftover primary references (already packed)", EditorStyles.wordWrappedMiniLabel);
                                if (entry.CleanupDetail) EditorGUILayout.LabelField("Clear leftover detail references (already packed)", EditorStyles.wordWrappedMiniLabel);
                                EditorGUILayout.LabelField(AssetDatabase.GetAssetPath(entry.Material), EditorStyles.wordWrappedMiniLabel);
                            }
                        notes = EditorGUILayout.Foldout(notes, $"Skipped / scan notes ({preview.Notes.Count})", true);
                        if (notes) foreach (string note in preview.Notes) EditorGUILayout.LabelField(note, EditorStyles.wordWrappedLabel);
                    }
                    int count = preview.Entries.Count(e => e.Included);
                    using (new EditorGUI.DisabledScope(count == 0))
                        if (GUILayout.Button($"Pack / clean selected materials ({count})")) Apply();
                }
                if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
            }
        }

        private void Scan()
        {
            preview = null;
            try { adapter = new MochieScenePacker.Adapter(); preview = MochieScenePacker.Collect(SceneMaterials.Active(), includeDetail); message = null; }
            catch (Exception ex) { message = ex.GetBaseException().Message; }
        }

        private void Apply()
        {
            try { SceneMaterials.RequireActive(preview.Scene); }
            catch (Exception ex) { message = ex.Message; return; }
            if (!EditorUtility.DisplayDialog("Pack Mochie Scene Materials",
                $"Pack or clean {preview.Entries.Count(e => e.Included)} selected material(s) in '{preview.Scene.name}'?\n\n" +
                "Matching inputs share one new PNG, saved beside the first matching material. Shared materials change in their other uses too. " +
                "Separate data-map references are cleared after successful packing, or from valid already-packed workflows. " +
                "Texture files are retained. Undo restores material settings and references but keeps generated PNGs. Review before saving.", "Pack / clean", "Cancel")) return;
            try
            {
                var result = MochieScenePacker.Apply(preview, adapter, name => EditorUtility.DisplayCancelableProgressBar("Packing Mochie materials", name, 0.5f));
                message = $"{result.Changed} materials updated; {result.Outputs.Count} PNGs created; {result.Reused} packs reused; " +
                    $"{result.ClearedReferences} source references cleared; {result.Errors.Count} failed." +
                    (result.Cancelled ? " Cancelled; completed changes are retained." : "") + " Review the scene, then save. Undo restores material settings and references.";
                Debug.Log("[Lightbulb] " + message + "\nGenerated textures:\n" + string.Join("\n", result.Outputs));
                foreach (string error in result.Errors) Debug.LogError("[Lightbulb] " + error + " (material restored; any generated PNGs remain)");
                preview = null;
            }
            catch (Exception ex) { message = ex.GetBaseException().Message; Debug.LogException(ex); }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
}
