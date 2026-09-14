using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.WorldTools
{
    internal sealed class DisabledObjectCleanupWindow : EditorWindow
    {
        private DisabledObjectCleanup.Scan scan;
        [SerializeField] private List<DisabledObjectCleanup.TagChange> changes = new List<DisabledObjectCleanup.TagChange>();
        private Vector2 scroll;
        private string message;
        private string filter = "";

        [MenuItem("Tools/Lightbulb/Find Unreferenced Disabled Objects")]
        private static void Open()
        {
            var window = GetWindow<DisabledObjectCleanupWindow>("Disabled Objects");
            window.minSize = new Vector2(620, 440);
        }

        private bool Progress(string name) => EditorUtility.DisplayCancelableProgressBar("Scanning scene references", name, 0.5f);

        private void Run(Action action)
        {
            try { action(); }
            catch (OperationCanceledException) { message = "Scan cancelled. Nothing changed."; }
            catch (Exception ex) { message = ex.Message; }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Disabled objects with no known references", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Marking EditorOnly excludes the object and its children from builds. Exclude objects your scripts find by name or tag.",
                EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(!MaterialTextureBatch.IsIdle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Scan active scene")) Run(() => { scan = null; scan = DisabledObjectCleanup.Collect(Progress); message = null; });
                    if (GUILayout.Button("Select all") && scan != null) foreach (var entry in scan.Candidates) entry.Included = true;
                    if (GUILayout.Button("Select none") && scan != null) foreach (var entry in scan.Candidates) entry.Included = false;
                }
                filter = EditorGUILayout.TextField("Filter results", filter);
                if (scan != null)
                {
                    var candidates = scan.Candidates.ToList();
                    if (scan.Uncertainties.Count > 0)
                        EditorGUILayout.HelpBox("Could not finish checking references. Fix these scan issues and scan again:\n\n" +
                            string.Join("\n", scan.Uncertainties), MessageType.Warning);
                    else
                        EditorGUILayout.LabelField($"{candidates.Count} unreferenced disabled objects | {candidates.Count(e => e.Included)} selected");
                    scroll = EditorGUILayout.BeginScrollView(scroll);
                    if (scan.Uncertainties.Count == 0 && candidates.Count == 0)
                        EditorGUILayout.LabelField("No unreferenced disabled objects found.", EditorStyles.wordWrappedLabel);
                    foreach (var entry in candidates)
                    {
                        if (SceneReferenceScan.Describe(entry.Object).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            entry.Included = EditorGUILayout.Toggle(entry.Included, GUILayout.Width(18));
                            EditorGUILayout.ObjectField(entry.Object, typeof(GameObject), true);
                        }
                    }
                    EditorGUILayout.EndScrollView();
                    using (new EditorGUI.DisabledScope(!candidates.Any(e => e.Included)))
                        if (GUILayout.Button("Mark selected EditorOnly")) Run(() =>
                        {
                            var applied = DisabledObjectCleanup.Apply(scan, Progress);
                            // Preserve the earliest original tag across Undo/reapply operations.
                            foreach (var change in applied) if (!changes.Any(c => c.Object == change.Object)) changes.Add(change);
                            scan = null;
                            message = $"Marked {applied.Count} branch roots EditorOnly. Scene remains unsaved; Edit > Undo is available.";
                        });
                }
                using (new EditorGUI.DisabledScope(changes.Count == 0))
                    if (GUILayout.Button("Restore original tags from this window")) Run(() =>
                    {
                        int restored = DisabledObjectCleanup.Restore(changes);
                        scan = null;
                        message = $"Restored {restored} original tags in the active scene.";
                    });
                EditorGUILayout.LabelField("Restore records survive script reloads while this window stays open. Edit > Undo also restores tags. " +
                    "Objects whose tags were changed afterward are left alone.", EditorStyles.wordWrappedMiniLabel);
            }
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
        }
    }
}
