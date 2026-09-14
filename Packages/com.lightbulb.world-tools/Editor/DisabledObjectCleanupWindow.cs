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
            EditorGUILayout.HelpBox("Finds explicitly disabled branches in the active scene with no detected external references. " +
                "EditorOnly excludes the whole branch from builds. Runtime name/tag lookups and custom code cannot be proven unused; " +
                "exclude anything you intend to activate that way. No objects are deleted.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!MaterialTextureBatch.IsIdle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Scan active scene")) Run(() => { scan = null; scan = DisabledObjectCleanup.Collect(Progress); message = null; });
                    if (GUILayout.Button("Select candidates") && scan != null) foreach (var entry in scan.Entries) entry.Included = entry.Candidate;
                    if (GUILayout.Button("Select none") && scan != null) foreach (var entry in scan.Entries) entry.Included = false;
                }
                filter = EditorGUILayout.TextField("Filter results", filter);
                if (scan != null)
                {
                    EditorGUILayout.LabelField($"{scan.Scene.name}: {scan.Entries.Count} disabled branches | " +
                        $"{scan.Entries.Count(e => e.Candidate)} candidates | {scan.Entries.Count(e => e.Included)} selected");
                    scroll = EditorGUILayout.BeginScrollView(scroll);
                    foreach (string issue in scan.Uncertainties) EditorGUILayout.HelpBox(issue, MessageType.Warning);
                    foreach (var entry in scan.Entries)
                    {
                        if (entry.Object == null || SceneReferenceScan.Describe(entry.Object).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                using (new EditorGUI.DisabledScope(!entry.Candidate)) entry.Included = EditorGUILayout.Toggle(entry.Included, GUILayout.Width(18));
                                EditorGUILayout.ObjectField(entry.Object, typeof(GameObject), true);
                            }
                            EditorGUILayout.LabelField(SceneReferenceScan.Describe(entry.Object), EditorStyles.wordWrappedMiniLabel);
                            EditorGUILayout.LabelField(entry.Candidate ? "No detected external references (including descendants)." :
                                string.Join("\n", entry.Reasons.Distinct()), EditorStyles.wordWrappedLabel);
                        }
                    }
                    EditorGUILayout.EndScrollView();
                    using (new EditorGUI.DisabledScope(!scan.Entries.Any(e => e.Included && e.Candidate)))
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
