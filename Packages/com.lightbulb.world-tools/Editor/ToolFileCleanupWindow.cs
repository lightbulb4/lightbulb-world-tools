using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.WorldTools
{
    internal sealed class ToolFileCleanupWindow : EditorWindow
    {
        ToolFileCleanup.Preview preview;
        bool legacyPacks, removeSceneBackup, showOriginals;
        Vector2 scroll;
        string message;
        [MenuItem("Tools/Lightbulb/Clean Up Tool Files")]
        internal static void Open() => GetWindow<ToolFileCleanupWindow>("Clean Up Tool Files").Show();
        void Run(Action action) { try { action(); } catch (OperationCanceledException e) { message = e.Message; } catch (Exception e) { message = e.Message; Debug.LogException(e); } finally { EditorUtility.ClearProgressBar(); } }
        void OnGUI()
        {
            EditorGUILayout.LabelField("Clean Up Tool Files", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Keeps the earliest available original backup for each asset. Lists redundant backups and generated assets with no known project references. Nothing is deleted by scanning.", MessageType.Info);
            EditorGUI.BeginChangeCheck();
            legacyPacks = EditorGUILayout.ToggleLeft("Include older packed filenames for manual review", legacyPacks);
            removeSceneBackup = EditorGUILayout.ToggleLeft("Remove the finished swapper's pre-swap scene backup", removeSceneBackup);
            if (EditorGUI.EndChangeCheck()) preview = null;
            EditorGUILayout.LabelField("Original texture files are retained. Legacy filenames and runtime name-based loads require your review. Close Prefab Mode and save scenes first.", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Scan for files to clean up")) Run(() => { message = null; preview = ToolFileCleanup.Scan(legacyPacks, removeSceneBackup); });
            if (preview != null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Select all listed files")) foreach (var entry in preview.Entries) entry.Included = true;
                    if (GUILayout.Button("Select none")) foreach (var entry in preview.Entries) entry.Included = false;
                }
                using (var view = new EditorGUILayout.ScrollViewScope(scroll))
                {
                    scroll = view.scrollPosition;
                    foreach (string note in preview.Notes) EditorGUILayout.HelpBox(note, MessageType.Info);
                    showOriginals = EditorGUILayout.Foldout(showOriginals, "Original backups kept (" + preview.Originals.Count + ")");
                    if (showOriginals)
                        foreach (var copy in preview.Originals)
                        {
                            EditorGUILayout.LabelField(copy.Source, EditorStyles.wordWrappedLabel);
                            if (GUILayout.Button("Show backup: " + copy.Path)) EditorUtility.RevealInFinder(copy.Path);
                        }
                    if (preview.Entries.Count == 0) EditorGUILayout.LabelField("No files need cleanup.");
                    foreach (var entry in preview.Entries)
                    {
                        entry.Included = EditorGUILayout.ToggleLeft(entry.Path, entry.Included);
                        EditorGUILayout.LabelField(entry.Reason, EditorStyles.wordWrappedMiniLabel);
                    }
                }
                int count = preview.Entries.Count(e => e.Included);
                using (new EditorGUI.DisabledScope(count == 0))
                    if (GUILayout.Button("Delete " + count + " selected files…")) Run(() =>
                    {
                        if (!EditorUtility.DisplayDialog("Delete the selected tool files?", "Permanently delete these " + count + " files? This is not Unity Undo. Original backups shown as kept will remain.\n\n" + string.Join("\n", preview.Entries.Where(e => e.Included).Select(e => e.Path)), "Delete selected files", "Cancel")) return;
                        int removed = ToolFileCleanup.Apply(preview); preview = null;
                        message = "Deleted " + removed + " files.";
                    });
            }
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
        }
    }
}
