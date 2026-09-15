using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.WorldTools
{
    internal sealed class MochieBakedSpecularWindow : EditorWindow
    {
        private MochieSpecularPatch.Inspection patch;
        private MochieBakedSpecular.Preview preview;
        private Vector2 scroll;
        private string message;
        private bool showNotes;

        [MenuItem("Tools/Lightbulb/Mochie Baked Specular")]
        internal static void Open() { GetWindow<MochieBakedSpecularWindow>("Mochie Baked Specular").minSize = new Vector2(620, 440); }
        private void OnEnable() { RefreshPatch(); Undo.undoRedoPerformed += Invalidate; }
        private void OnDisable() { Undo.undoRedoPerformed -= Invalidate; }
        private void Invalidate() { preview = null; Repaint(); }
        private void RefreshPatch() { patch = MochieSpecularPatch.Inspect(); }
        private void Run(Action action)
        {
            try { action(); }
            catch (Exception ex) { message = ex.Message; Debug.LogWarning("[Lightbulb Mochie specular] " + message); }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Mochie baked specular", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Adds approximate Dominant Direction highlights to Standard / Standard Lite using existing lightmaps. " +
                "Keep Bakery Mode = None for Dominant Direction. No rebake or material conversion is needed.", MessageType.Info);
            using (new EditorGUI.DisabledScope(!MochieSpecularPatch.IsIdle))
            {
                EditorGUILayout.LabelField("1. Shader patch", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(patch?.Message ?? "Check the installed source.", patch != null && patch.Compatible ? MessageType.Info : MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Check installed Mochie")) Run(RefreshPatch);
                    using (new EditorGUI.DisabledScope(patch == null || !patch.Compatible || patch.Patched))
                        if (GUILayout.Button("Install patch")) Run(() =>
                        {
                            if (!EditorUtility.DisplayDialog("Patch installed Mochie?", "This edits " + patch.Path +
                                ". A backup is kept in Library/LightbulbWorldTools. Shader references and materials are unchanged. " +
                                "Existing enabled Bakery Specular Highlights will start working with Dominant Direction too.", "Install", "Cancel")) return;
                            message = MochieSpecularPatch.SetInstalled(patch.Path, true);
                            RefreshPatch(); Invalidate();
                        });
                    using (new EditorGUI.DisabledScope(patch == null || !patch.Patched))
                        if (GUILayout.Button("Remove patch")) Run(() =>
                        {
                            if (!EditorUtility.DisplayDialog("Remove specular patch?", "Remove only Lightbulb's exact shader addition and turn off auto-reapply? Material toggles remain unchanged.", "Remove", "Cancel")) return;
                            MochieSpecularPatchSettings.instance.Configure(patch.Path, false);
                            message = MochieSpecularPatch.SetInstalled(patch.Path, false);
                            RefreshPatch(); Invalidate();
                        });
                }
                var settings = MochieSpecularPatchSettings.instance;
                using (new EditorGUI.DisabledScope(!settings.autoReapply && (patch == null || !patch.Compatible || !patch.Patched)))
                {
                    bool automatic = EditorGUILayout.Toggle("Reapply after compatible updates", settings.autoReapply);
                    if (automatic != settings.autoReapply) Run(() => settings.Configure(patch.Path, automatic));
                }
                EditorGUILayout.LabelField("Opt-in per project. Unknown source versions stop with a warning; they are never patched blindly. Remove before uninstalling this tool.", EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("2. Material preview", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("Scans active-scene renderer materials, including inactive objects. Only materials with compatible baked data are eligible. " +
                    "Recommended = scalar roughness 0.1–0.9 (exclusive at 0.9); textured, very smooth/rough, transparent or special cases need review. " +
                    "Zero roughness is smooth and reflective, not non-reflective. Metallic is not required.", MessageType.Info);
                if (GUILayout.Button("Scan active scene")) Run(() => { RefreshPatch(); preview = MochieBakedSpecular.Collect(SceneMaterials.Active()); message = null; });
                if (preview != null)
                {
                    EditorGUILayout.LabelField($"{preview.Entries.Count} eligible / {preview.Entries.Count(e => e.Recommended)} recommended / {preview.Notes.Count} skipped");
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Recommended only")) foreach (var e in preview.Entries) e.Included = e.Recommended;
                        if (GUILayout.Button("All eligible (including review)")) foreach (var e in preview.Entries) e.Included = true;
                        if (GUILayout.Button("None")) foreach (var e in preview.Entries) e.Included = false;
                    }
                    using (var view = new EditorGUILayout.ScrollViewScope(scroll))
                    {
                        scroll = view.scrollPosition;
                        foreach (var entry in preview.Entries)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                entry.Included = EditorGUILayout.Toggle(entry.Included, GUILayout.Width(18));
                                EditorGUILayout.ObjectField(entry.Material, typeof(Material), false);
                            }
                            EditorGUILayout.LabelField(entry.Reason, EditorStyles.wordWrappedMiniLabel);
                        }
                        showNotes = EditorGUILayout.Foldout(showNotes, $"Skipped ({preview.Notes.Count})", true);
                        if (showNotes) foreach (var note in preview.Notes) EditorGUILayout.LabelField(note, EditorStyles.wordWrappedMiniLabel);
                    }
                    int count = preview.Entries.Count(e => e.Included);
                    using (new EditorGUI.DisabledScope(count == 0))
                        if (GUILayout.Button($"Enable baked highlights on {count} materials")) Run(() =>
                        {
                            if (!EditorUtility.DisplayDialog("Enable baked highlights?", $"Update {count} shared material assets? Changes affect their other scenes/prefabs too. " +
                                "Only the Bakery Specular Highlights toggle/keyword changes. Strength, roughness, metallic, regular highlights, and Bakery Mode stay untouched. " +
                                "Undo restores changes. Review before saving.", "Enable", "Cancel")) return;
                            message = $"Enabled on {MochieBakedSpecular.Apply(preview)} materials. Review before saving; Undo restores material settings.";
                            preview = null;
                        });
                }
            }
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
        }
    }
}
