using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.WorldTools
{
    internal sealed class SceneTextureCrunchWindow : EditorWindow
    {
        private static readonly int[] SelectionSizes = { 4096, 2048, 1024, 512 };
        private static int ImportedSize(MaterialTextureBatch.Entry entry) => entry.Texture == null ? 0 : Math.Max(entry.Texture.width, entry.Texture.height);
        [SerializeField] private bool enable = true;
        [SerializeField] private int quality = 50;
        [SerializeField] private bool onlyWithoutCrunch;
        private Scene scene;
        private List<MaterialTextureBatch.Entry> entries = new List<MaterialTextureBatch.Entry>();
        private string message;
        private Vector2 scroll;
        private string filter = "";
        private bool hasPreview;
        private bool showSkipped;

        private static bool IsSkipNote(string note) => note.IndexOf("skipped", StringComparison.OrdinalIgnoreCase) >= 0;

        [MenuItem("Tools/Lightbulb/Scene Texture Crunch Compression")]
        private static void Open()
        {
            var window = GetWindow<SceneTextureCrunchWindow>("Scene Texture Crunch");
            window.minSize = new Vector2(600, 420);
        }

        private SceneReferenceScan.Result Discover() => SceneReferenceScan.Collect(new[] { scene }, name =>
            EditorUtility.DisplayCancelableProgressBar("Scanning scene texture references", name, 0.5f));

        private void Scan()
        {
            entries.Clear();
            hasPreview = false;
            showSkipped = false;
            scene = SceneMaterials.Active();
            var found = Discover();
            entries = MaterialTextureBatch.CollectTextures(found.Objects.OfType<Texture>(),
                enable ? MaterialTextureBatch.CrunchMode.Enable : MaterialTextureBatch.CrunchMode.Disable, quality, enable && onlyWithoutCrunch);
            entries.Sort((a, b) =>
            {
                int size = ImportedSize(b).CompareTo(ImportedSize(a));
                return size != 0 ? size : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            hasPreview = true;
            message = found.Uncertainties.Count == 0 ? null : "Some dependencies could not be inspected:\n" + string.Join("\n", found.Uncertainties);
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (OperationCanceledException) { message = "Scan cancelled. Nothing changed."; }
            catch (Exception ex) { message = ex.Message; }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private void Apply()
        {
            SceneMaterials.RequireActive(scene);
            var current = new HashSet<Texture>(Discover().Objects.OfType<Texture>());
            if (entries.Any(e => e.Included && !current.Contains(e.Texture)))
                throw new InvalidOperationException("Scene texture references changed. Scan again before applying.");
            var result = MaterialTextureBatch.Apply(entries, (i, count, path) =>
                EditorUtility.DisplayCancelableProgressBar("Updating scene texture compression", path, (float)i / count));
            entries.Clear();
            hasPreview = false;
            message = $"{result.Changed} changed; {result.Failed} failed." +
                (result.Cancelled ? " Cancelled; completed changes remain applied." : "") +
                (result.BackupRoot == null ? "" : "\nOriginal import settings: " + result.BackupRoot);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Scans active-scene dependencies, including inactive objects, materials, terrain, UI, animation swaps and serialized script references. " +
                "Dynamically loaded textures may not be discoverable. Shared texture import settings change everywhere those assets are used.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!MaterialTextureBatch.IsIdle))
            {
                EditorGUI.BeginChangeCheck();
                enable = EditorGUILayout.Popup("Crunch compression", enable ? 0 : 1, new[] { "Enable", "Disable" }) == 0;
                if (enable)
                {
                    onlyWithoutCrunch = EditorGUILayout.Toggle("Crunch currently off only", onlyWithoutCrunch);
                    quality = EditorGUILayout.IntSlider("Crunch quality", quality, 0, 100);
                    if (onlyWithoutCrunch)
                        EditorGUILayout.LabelField("Only enable textures with Crunch off in Default and all enabled platform overrides. Existing Crunch qualities are left alone.",
                            EditorStyles.wordWrappedMiniLabel);
                }
                if (EditorGUI.EndChangeCheck()) { entries.Clear(); hasPreview = false; message = "Settings changed. Scan again to preview."; }
                EditorGUILayout.LabelField("Higher quality means larger files and longer imports. Crunch reduces download size, not VRAM. " +
                    "Resolution is preserved. Default and existing enabled platform overrides are updated where supported.", EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Scan active scene")) Run(Scan);
                    if (GUILayout.Button("Select all")) foreach (var entry in entries) entry.Included = entry.Changes.Count > 0;
                    if (GUILayout.Button("Select none")) foreach (var entry in entries) entry.Included = false;
                }
                if (hasPreview)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label("Select only (longest side):", GUILayout.Width(165));
                        foreach (int size in SelectionSizes)
                            if (GUILayout.Button(size.ToString()))
                                foreach (var entry in entries)
                                    entry.Included = entry.Changes.Count > 0 && ImportedSize(entry) == size;
                    }
                filter = EditorGUILayout.TextField("Filter textures", filter);
                var changing = entries.Where(e => e.Changes.Count > 0).ToList();
                var skipped = entries.Where(e => e.Changes.Count == 0 && e.Notes.Any(IsSkipNote)).ToList();
                int count = changing.Count(e => e.Included);
                int filtered = entries.Count(e => e.ExcludedByCrunchFilter);
                if (hasPreview)
                {
                    EditorGUILayout.LabelField("Proposed changes — not applied yet", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField($"{changing.Count} textures need changes | {count} selected | " +
                        $"{entries.Count - changing.Count - skipped.Count - filtered} already match | {skipped.Count} skipped" +
                        (enable && onlyWithoutCrunch ? $" | {filtered} already use Crunch" : ""));
                }
                scroll = EditorGUILayout.BeginScrollView(scroll);
                if (hasPreview && changing.Count == 0)
                    EditorGUILayout.LabelField("No texture changes to apply.", EditorStyles.wordWrappedLabel);
                foreach (var entry in changing)
                {
                    if ((entry.Path + " " + entry.Texture?.name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            entry.Included = EditorGUILayout.Toggle(entry.Included, GUILayout.Width(18));
                            EditorGUILayout.ObjectField(entry.Texture, typeof(Texture), false);
                        }
                        EditorGUILayout.LabelField(entry.Path, EditorStyles.wordWrappedMiniLabel);
                        EditorGUILayout.LabelField(string.Join("\n", entry.Notes), EditorStyles.wordWrappedLabel);
                    }
                }
                if (skipped.Count > 0)
                {
                    showSkipped = EditorGUILayout.Foldout(showSkipped, $"Skipped textures ({skipped.Count})", true);
                    if (showSkipped)
                        foreach (var entry in skipped)
                        {
                            if ((entry.Path + " " + entry.Texture?.name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            EditorGUILayout.ObjectField(entry.Texture, typeof(Texture), false);
                            EditorGUILayout.LabelField(string.Join("\n", entry.Notes.Where(IsSkipNote)), EditorStyles.wordWrappedMiniLabel);
                        }
                }
                EditorGUILayout.EndScrollView();
                if (hasPreview) EditorGUILayout.LabelField("Size buttons replace the selection, including results hidden by the text filter.", EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUI.DisabledScope(count == 0))
                    if (GUILayout.Button($"Apply to {count} textures")) Run(Apply);
                EditorGUILayout.LabelField("Original images are untouched. The first original import settings per texture are retained under Library/LightbulbWorldTools. " +
                    "Restore by closing Unity and copying the backup .meta files to matching project paths. This is not Unity Undo.", EditorStyles.wordWrappedMiniLabel);
            }
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
        }
    }
}
