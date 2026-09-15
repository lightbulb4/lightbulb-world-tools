using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.WorldTools
{
    internal sealed class EmptyMaterialMapsWindow : EditorWindow
    {
        [SerializeField] private bool fuzzy;
        private static readonly int[] SelectionSizes = { 4096, 2048, 1024, 512 };
        private EmptyMaterialMaps.Scan scan;
        private Vector2 scroll;
        private string search = "";
        private string message;
        private bool showNotes;
        private Scene scannedScene;

        [MenuItem("Tools/Lightbulb/Find Empty Material Maps")]
        internal static void Open()
        {
            var window = GetWindow<EmptyMaterialMapsWindow>("Empty Material Maps");
            window.minSize = new Vector2(650, 460);
            window.Show();
        }

        private void OnEnable() { Undo.undoRedoPerformed += Invalidate; }
        private void OnDisable() { Undo.undoRedoPerformed -= Invalidate; }
        private void Invalidate() { scan = null; message = "Materials changed by Undo/Redo. Scan again to refresh references."; Repaint(); }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Find Empty Material Maps", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Metallic, roughness / smoothness, AO, normal, height, and packed data maps", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.HelpBox("Finds constant textures, including solid black, solid white, and flat normal maps. " +
                "Scans materials assigned to renderers, terrains, and skyboxes in the active scene, including inactive objects. " +
                "Remove clears matching slots on those materials. Shared material assets also change wherever else they are used. " +
                "Files are kept. Changes support Undo; save the project when satisfied.", MessageType.Info);
            EditorGUILayout.HelpBox("A constant map can still affect appearance. Removal uses the shader's unassigned-map defaults; " +
                "sliders are not adjusted to compensate. Review the scene after removing maps. " +
                "Fuzzy matching deliberately discards the remaining pixels.", MessageType.Warning);

            using (new EditorGUI.DisabledScope(!MaterialTextureBatch.IsIdle))
            {
                EditorGUI.BeginChangeCheck();
                fuzzy = EditorGUILayout.Toggle("Fuzzy matching (99.99%)", fuzzy);
                if (EditorGUI.EndChangeCheck()) { scan = null; message = "Settings changed. Scan again."; }
                EditorGUILayout.LabelField("Checks every pixel at the current imported resolution, including alpha. " +
                    "Exact pixel values; no thumbnail sampling or color tolerance. Normal values use GPU channel packing.", EditorStyles.wordWrappedMiniLabel);
                if (GUILayout.Button("Scan active scene")) RunScan();

                if (scan != null)
                {
                    EditorGUILayout.LabelField($"{scannedScene.name} | {scan.Checked} maps checked | {scan.Entries.Count} candidates | {scan.Notes.Count} scan notes");
                    search = EditorGUILayout.TextField("Filter results", search);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Select all removable")) foreach (var entry in scan.Entries) entry.Included = CanRemove(entry);
                        if (GUILayout.Button("Select none")) foreach (var entry in scan.Entries) entry.Included = false;
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label("Select only (longest side):", GUILayout.Width(165));
                        foreach (int size in SelectionSizes)
                            if (GUILayout.Button(size.ToString()))
                                foreach (var entry in scan.Entries)
                                    entry.Included = CanRemove(entry) && EmptyMaterialMaps.ImportedSize(entry) == size;
                    }
                    EmptyMaterialMaps.Entry removeOne = null;
                    using (var view = new EditorGUILayout.ScrollViewScope(scroll))
                    {
                        scroll = view.scrollPosition;
                        foreach (var entry in scan.Entries)
                        {
                            if (entry.Texture == null) continue;
                            if (!string.IsNullOrEmpty(search) && (entry.Path + " " + string.Join(" ", entry.Uses.Select(u => u.Label + " " + u.Kind)))
                                .IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                            {
                                bool removable = CanRemove(entry);
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    using (new EditorGUI.DisabledScope(!removable)) entry.Included = EditorGUILayout.Toggle(entry.Included, GUILayout.Width(18));
                                    GUILayout.Label(AssetPreview.GetMiniThumbnail(entry.Texture), GUILayout.Width(40), GUILayout.Height(40));
                                    EditorGUILayout.ObjectField(entry.Texture, typeof(Texture2D), false);
                                    using (new EditorGUI.DisabledScope(!removable))
                                        if (GUILayout.Button("Remove", GUILayout.Width(80))) removeOne = entry;
                                }
                                EditorGUILayout.LabelField(entry.Path, EditorStyles.wordWrappedMiniLabel);
                                EditorGUILayout.LabelField($"{entry.Texture.width} × {entry.Texture.height} | " +
                                    $"{entry.Analysis.Percent:0.####}% identical ({entry.Analysis.Matching:N0}/{entry.Analysis.Total:N0} pixels)");
                                EditorGUILayout.LabelField("Constant sampled RGBA: " + entry.Analysis.Value.ToString("F4"), EditorStyles.miniLabel);
                                entry.Expanded = EditorGUILayout.Foldout(entry.Expanded, $"{entry.Uses.Count} material references (all cleared on removal)", true);
                                if (entry.Expanded)
                                    foreach (var use in entry.Uses)
                                    {
                                        EditorGUILayout.ObjectField(use.Material, typeof(Material), false);
                                        EditorGUILayout.LabelField(use.Property + " — " + (use.Kind ?? "Other / saved reference"), EditorStyles.miniLabel);
                                        EditorGUILayout.LabelField(use.Label, EditorStyles.wordWrappedMiniLabel);
                                    }
                                if (!removable)
                                    EditorGUILayout.HelpBox("Cannot remove from every reference: " + string.Join("; ", entry.Uses
                                        .Where(u => u.Material == null || u.EditBlock != null)
                                        .Select(u => u.Material == null ? "Material no longer exists; rescan" : u.Material.name + ": " + u.EditBlock).Distinct()), MessageType.Warning);
                            }
                        }
                        showNotes = EditorGUILayout.Foldout(showNotes, $"Scan notes ({scan.Notes.Count})", true);
                        if (showNotes) foreach (string note in scan.Notes) EditorGUILayout.LabelField(note, EditorStyles.wordWrappedLabel);
                    }
                    var selected = scan.Entries.Where(e => e.Included && CanRemove(e)).ToList();
                    bool removeAll;
                    using (new EditorGUI.DisabledScope(selected.Count == 0))
                        removeAll = GUILayout.Button($"Remove all selected ({selected.Count})");
                    EditorGUILayout.LabelField("Selection includes results hidden by the filter. Other asset or script references can keep a texture in the build.", EditorStyles.wordWrappedMiniLabel);
                    if (removeOne != null) Apply(new List<EmptyMaterialMaps.Entry> { removeOne });
                    else if (removeAll) Apply(selected);
                }
                if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
            }
        }

        private static bool CanRemove(EmptyMaterialMaps.Entry entry) => entry.Texture != null &&
            entry.Uses.All(u => u.Material != null && u.EditBlock == null);

        private static bool Cancel(string detail) => EditorUtility.DisplayCancelableProgressBar("Scanning material maps", detail, 0.5f);

        private void RunScan()
        {
            scan = null;
            try
            {
                scannedScene = SceneMaterials.Active();
                scan = EmptyMaterialMaps.Collect(SceneMaterials.Collect(scannedScene, Cancel), fuzzy ? EmptyMaterialMaps.FuzzyMinimumPercent : 100, Cancel);
                foreach (var entry in scan.Entries) entry.Included = CanRemove(entry);
                message = scan.Entries.Count == 0 ? "No matching maps found. Expand scan notes for any skipped textures." : null;
            }
            catch (OperationCanceledException) { message = "Scan cancelled. No materials changed."; }
            catch (Exception ex) { message = "Scan failed: " + ex.Message; Debug.LogException(ex); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private void Apply(List<EmptyMaterialMaps.Entry> entries)
        {
            try { SceneMaterials.RequireActive(scannedScene); }
            catch (Exception ex) { message = ex.Message; return; }
            int references = entries.Sum(e => e.Uses.Count);
            if (!EditorUtility.DisplayDialog("Remove Empty Material Maps",
                $"Clear {references} material references to {entries.Count} texture(s) on materials in '{scannedScene.name}'?\n\n" +
                "Shared material assets also change in other scenes or prefabs that use them. " +
                "This includes every material slot shown in the preview, even other map types. Files stay on disk. " +
                "Shader defaults can change the appearance, and fuzzy matches discard real pixels. " +
                "Use Edit > Undo to restore all references. Materials are not automatically saved.", "Remove references", "Cancel")) return;
            try
            {
                // A per-row action works even when that row's bulk checkbox is off.
                var inclusion = entries.Select(e => e.Included).ToArray();
                int changed;
                try
                {
                    foreach (var entry in entries) entry.Included = true;
                    SceneMaterials.RequireActive(scannedScene);
                    changed = EmptyMaterialMaps.Remove(entries, SceneMaterials.Collect(scannedScene, Cancel));
                }
                finally { for (int i = 0; i < entries.Count; i++) entries[i].Included = inclusion[i]; }
                foreach (var entry in entries) scan.Entries.Remove(entry);
                message = $"Cleared {changed} references. Files kept. Review the scene, then save the project; Edit > Undo restores references.";
            }
            catch (OperationCanceledException) { message = "Removal cancelled before changes."; }
            catch (Exception ex) { message = ex.Message; Debug.LogException(ex); }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
}
