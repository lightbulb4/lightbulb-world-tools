using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.WorldTools
{
    internal sealed class SceneNormalMapFormatWindow : EditorWindow
    {
        [SerializeField] private SceneNormalMapFormat.Preset preset = SceneNormalMapFormat.Preset.DownloadSize;
        private Scene scene;
        private List<MaterialTextureBatch.Entry> entries = new List<MaterialTextureBatch.Entry>();
        private Vector2 scroll, messageScroll;
        private string filter = "", message;
        private bool hasPreview, showSkipped, showMessages;
        private int restoreCount;

        [MenuItem("Tools/Lightbulb/Scene Normal Map Format")]
        private static void Open()
        {
            GetWindow<SceneNormalMapFormatWindow>("Scene Normal Map Format").minSize = new Vector2(620, 440);
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Scene Normal Map Format");
            minSize = new Vector2(620, 440);
            Run(RefreshRestoreCount);
        }
        private void RefreshRestoreCount() => restoreCount = SceneNormalMapFormat.Load().Records.Count;
        private SceneReferenceScan.Result Discover() => SceneReferenceScan.Collect(new[] { scene }, name =>
            EditorUtility.DisplayCancelableProgressBar("Scanning scene materials", name, 0.5f));

        private void Scan()
        {
            entries.Clear();
            hasPreview = false;
            scene = SceneMaterials.Active();
            var found = Discover();
            entries = SceneNormalMapFormat.CollectPreset(found.Objects.OfType<Material>(), preset);
            hasPreview = true;
            var warnings = found.Uncertainties.Concat(found.Warnings).ToArray();
            message = warnings.Length == 0 ? null : "Some dependencies could not be inspected:\n" + string.Join("\n", warnings);
            showMessages = message != null;
            RefreshRestoreCount();
        }

        private void Apply()
        {
            SceneMaterials.RequireActive(scene);
            var found = Discover();
            var current = SceneNormalMapFormat.CollectPreset(found.Objects.OfType<Material>(), preset);
            var guids = new HashSet<string>(current.Where(e => e.Changes.Count > 0).Select(e => e.Guid));
            if (entries.Any(e => e.Included && !guids.Contains(e.Guid)))
                throw new InvalidOperationException("Scene normal-map references or settings changed. Scan again before applying.");
            var result = SceneNormalMapFormat.Apply(entries, (i, count, path) =>
                EditorUtility.DisplayCancelableProgressBar("Converting normal maps", path, (float)i / count));
            Finish(result);
        }

        private void Restore()
        {
            Finish(SceneNormalMapFormat.Restore((i, count, path) =>
                EditorUtility.DisplayCancelableProgressBar("Restoring normal-map settings", path, (float)i / count)));
        }

        private void Finish(SceneNormalMapFormat.Result result)
        {
            entries.Clear();
            hasPreview = false;
            message = result.ToString();
            showMessages = true;
            RefreshRestoreCount();
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (OperationCanceledException) { message = "Scan cancelled. Nothing changed."; showMessages = true; }
            catch (Exception ex) { message = ex.Message; showMessages = true; }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private void OnGUI()
        {
            using (new EditorGUI.DisabledScope(!SceneMaterials.IsLightingIdle))
            {
                EditorGUI.BeginChangeCheck();
                preset = (SceneNormalMapFormat.Preset)EditorGUILayout.Popup("Preset", (int)preset, SceneNormalMapFormat.PresetLabels);
                bool crunch = preset == SceneNormalMapFormat.Preset.DownloadSize;
                if (EditorGUI.EndChangeCheck()) { entries.Clear(); hasPreview = false; }
                EditorGUILayout.HelpBox("Tip: Start with DXT5 + Crunch 100 for smaller downloads. If you notice visual artifacts, " +
                    "switch only the affected normal maps to BC5 using the Quality preset.", MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Scan active scene")) Run(Scan);
                    if (GUILayout.Button("Select all")) foreach (var entry in entries) entry.Included = entry.Changes.Count > 0;
                    if (GUILayout.Button("Select none")) foreach (var entry in entries) entry.Included = false;
                }
                filter = EditorGUILayout.TextField("Filter textures", filter);
                var changing = entries.Where(e => e.Changes.Count > 0).ToList();
                var skipped = entries.Where(e => e.Notes.Any(n => n.StartsWith("Skipped:", StringComparison.Ordinal))).ToList();
                int selected = changing.Count(e => e.Included);
                if (hasPreview)
                {
                    EditorGUILayout.LabelField("Proposed changes — not applied yet", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField($"{changing.Count} need changes | {selected} selected | {entries.Count - changing.Count - skipped.Count} already match | {skipped.Count} skipped");
                }
                using (var view = new EditorGUILayout.ScrollViewScope(scroll))
                {
                    scroll = view.scrollPosition;
                    foreach (var entry in changing)
                    {
                        if (entry.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                entry.Included = EditorGUILayout.Toggle(entry.Included, GUILayout.Width(18));
                                EditorGUILayout.ObjectField(entry.Texture, typeof(Texture), false);
                                GUILayout.Label(new GUIContent(entry.Uses.Count + " slots", string.Join("\n", entry.Uses)), GUILayout.Width(60));
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
                                EditorGUILayout.ObjectField(entry.Texture, typeof(Texture), false);
                                EditorGUILayout.LabelField(string.Join("\n", entry.Notes), EditorStyles.wordWrappedMiniLabel);
                            }
                    }
                    if (hasPreview && entries.Count == 0) EditorGUILayout.LabelField("No imported normal maps found on scene-referenced materials.", EditorStyles.wordWrappedLabel);
                }
                using (new EditorGUI.DisabledScope(selected == 0))
                    if (GUILayout.Button(crunch ? $"Apply Download Size preset to {selected} normal maps" : $"Apply Quality preset to {selected} normal maps")) Run(Apply);
                using (new EditorGUI.DisabledScope(restoreCount == 0))
                    if (GUILayout.Button($"Restore original desktop settings ({restoreCount} textures in this project)")) Run(Restore);
            }
            if (!string.IsNullOrEmpty(message))
            {
                showMessages = EditorGUILayout.Foldout(showMessages, "Messages", true);
                if (showMessages)
                    using (var view = new EditorGUILayout.ScrollViewScope(messageScroll, GUILayout.Height(Mathf.Min(110f, position.height * 0.2f))))
                    {
                        messageScroll = view.scrollPosition;
                        EditorGUILayout.HelpBox(message, MessageType.Info);
                    }
            }
        }
    }
}
