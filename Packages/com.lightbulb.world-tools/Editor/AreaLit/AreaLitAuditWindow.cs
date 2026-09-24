using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.AreaLitOcclusion
{
    // Extends the package's native Unity IMGUI workflow: scan, inspect a finding, choose one change.
    // The toolbar owns scanning; findings own object selection and explicit, undoable fixes.
    internal sealed partial class AreaLitAuditWindow : EditorWindow
    {
        [SerializeField] private bool includeDisabled;
        [SerializeField] private string assignmentProperty = "_LightMesh";
        private AreaLitAuditReport report;
        private Vector2 scroll;
        private bool stale;
        private string status;
        private MessageType statusSeverity;
        private readonly HashSet<string> expanded = new HashSet<string>();
        private readonly Dictionary<string, Texture> chosenTextures = new Dictionary<string, Texture>();
        private readonly Dictionary<string, string> preflight = new Dictionary<string, string>();

        [MenuItem("Tools/Lightbulb/AreaLit Configuration Audit")]
        public static void OpenWindow()
        {
            var window = GetWindow<AreaLitAuditWindow>();
            window.titleContent = new GUIContent("AreaLit Audit");
            window.minSize = new Vector2(540, 380);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += MarkStale;
            EditorApplication.projectChanged += MarkStale;
            Undo.undoRedoPerformed += MarkStale;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkStale;
            EditorApplication.projectChanged -= MarkStale;
            Undo.undoRedoPerformed -= MarkStale;
            ObjectChangeEvents.changesPublished -= OnObjectChanges;
        }

        private void OnObjectChanges(ref ObjectChangeEventStream stream)
        {
            if (report == null || stale) return;
            for (var index = 0; index < stream.length; index++)
            {
                switch (stream.GetEventType(index))
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                    case ObjectChangeKind.ChangeScene:
                    case ObjectChangeKind.UpdatePrefabInstances:
                        MarkStale();
                        return;
                }
            }
        }

        private void MarkStale()
        {
            stale = report != null;
            Repaint();
        }

        private bool RunAudit()
        {
            try
            {
                report = AreaLitAudit.Scan(includeDisabled);
                chosenTextures.Clear();
                preflight.Clear();
                stale = false;
                status = null;
                Repaint();
                return true;
            }
            catch (Exception exception)
            {
                stale = report != null;
                status = exception.Message;
                statusSeverity = MessageType.Error;
                Debug.LogException(exception);
            }
            Repaint();
            return false;
        }

        private void OnGUI()
        {
            var blocked = AreaLitAudit.BlockedReason;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("AreaLit Configuration Audit", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (stale)
                    GUILayout.Label(new GUIContent(EditorGUIUtility.IconContent("console.warnicon.sml").image,
                        "Results are out of date. Run audit to refresh."), GUILayout.Width(20));
                var runContent = new GUIContent("Run audit", stale ? EditorGUIUtility.IconContent("Refresh").image : null,
                    blocked ?? (stale ? "Refresh the audit before applying changes." : "Scan loaded scenes without changing them."));
                using (new EditorGUI.DisabledScope(blocked != null))
                    if (GUILayout.Button(runContent, EditorStyles.toolbarButton, GUILayout.Width(100))) RunAudit();
            }
            using (new EditorGUILayout.VerticalScope())
            {
                GUILayout.Space(8);
                EditorGUI.BeginChangeCheck();
                includeDisabled = EditorGUILayout.ToggleLeft("Include inactive objects and disabled components", includeDisabled);
                if (EditorGUI.EndChangeCheck()) MarkStale();
                if (blocked != null) EditorGUILayout.HelpBox(blocked, MessageType.Warning);
                if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, statusSeverity);
                if (report == null)
                {
                    GUILayout.Space(12);
                    GUILayout.Label("Run audit to check loaded scenes. Scanning changes nothing.", EditorStyles.wordWrappedLabel);
                    DrawUvRepairJournal(blocked != null);
                    return;
                }
                GUILayout.Label(report.Summary, EditorStyles.wordWrappedLabel);
                if (report.excludedDisabled > 0)
                    GUILayout.Label(report.excludedDisabled + " disabled material slot/projector use(s) excluded.", EditorStyles.wordWrappedMiniLabel);
                var configuration = report.findings.Count(item => !item.needsDecision && item.severity >= MessageType.Warning);
                var choices = report.findings.Count(item => item.needsDecision);
                GUILayout.Label(configuration + " configuration, " + choices + " review, " +
                    (report.findings.Count - configuration - choices) + " informational", EditorStyles.wordWrappedMiniLabel);
                if (report.materials == 0)
                    EditorGUILayout.HelpBox("No AreaLit materials found. Open a scene or include disabled objects.", MessageType.Info);
                else if (report.findings.Count == 0)
                    EditorGUILayout.HelpBox("No configuration findings. Visual quality is not measured.", MessageType.Info);

                scroll = EditorGUILayout.BeginScrollView(scroll);
                DrawAssignmentTool(blocked != null || stale);
                foreach (var finding in report.findings) DrawFinding(finding, blocked != null || stale);
                DrawOcclusionTools(blocked != null || stale);
                DrawUvRepairJournal(blocked != null);
                EditorGUILayout.EndScrollView();
                GUILayout.Space(4);
                GUILayout.Label("Changes require confirmation. Scenes are never auto-saved.", EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void DrawFinding(AreaLitAuditFinding finding, bool disableFix)
        {
            GUILayout.Space(8);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var title = (finding.needsDecision ? "Review · " : finding.severity + " · ") + finding.title;
                var key = "finding-details:" + finding.id;
                var unavailable = finding.fix == AreaLitAuditFix.None ? null : FindingReason(finding);
                var isExpanded = Foldout(key, title, true,
                    finding.target == null ? finding.recommendation : AreaLitAudit.Describe(finding.target));
                if (!string.IsNullOrEmpty(finding.summary))
                    GUILayout.Label(finding.summary, EditorStyles.wordWrappedLabel);
                if (finding.bindings != null || finding.fix != AreaLitAuditFix.None)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (finding.bindings != null)
                        {
                            if (GUILayout.Button("Edit assignments", GUILayout.Width(126)))
                            {
                                assignmentProperty = finding.bindings.property;
                                scroll = Vector2.zero;
                                Repaint();
                            }
                        }
                        else if (finding.layerChange != null && finding.layerChange.NeedsRole)
                        {
                            DrawProjectorRoleButton(finding, AreaLitProjectorRole.Players, "Use player setup…", disableFix);
                            DrawProjectorRoleButton(finding, AreaLitProjectorRole.Mirror, "Use mirror setup…", disableFix);
                        }
                        else
                        {
                            var label = finding.fix == AreaLitAuditFix.ResetMipBias ? "Reset to 0…" :
                                finding.fix == AreaLitAuditFix.OrderCaptureCamera ? "Set depth " + finding.proposedCameraDepth + "…" : "Fix now…";
                            using (new EditorGUI.DisabledScope(disableFix || unavailable != null))
                                if (GUILayout.Button(new GUIContent(label, unavailable ?? AreaLitAuditFixes.ChangeDescription(finding)), GUILayout.Width(126)))
                                    ApplyFix(finding);
                        }
                        GUILayout.FlexibleSpace();
                    }
                }
                if (finding.occlusionUvConflict) DrawUvConflictFinding(disableFix);
                if (!isExpanded) return;

                GUILayout.Space(4);
                if (finding.detail != finding.summary)
                    GUILayout.Label(finding.detail, EditorStyles.wordWrappedLabel);
                GUILayout.Label(finding.recommendation, EditorStyles.wordWrappedLabel);
                if (finding.target != null) DrawObject(finding.target);
                if (unavailable != null) GUILayout.Label(unavailable, EditorStyles.wordWrappedMiniLabel);
                if (finding.relatedObjects.Count > 0)
                    foreach (var target in finding.relatedObjects) DrawObject(target);
            }
        }

        private void ApplyFix(AreaLitAuditFinding finding)
        {
            var description = AreaLitAuditFixes.ChangeDescription(finding);
            if (!EditorUtility.DisplayDialog("Apply AreaLit configuration change?", AreaLitAudit.Describe(finding.target) + "\n\n" +
                    description + "\n\nUndo is available; nothing is auto-saved." +
                    (finding.target is Texture ? " Shared texture assets also affect materials outside the scanned scenes." : ""),
                    "Apply change", "Cancel")) return;
            RunOperation(() =>
            {
                AreaLitAuditFixes.Apply(finding, includeDisabled);
                return null;
            });
        }

        private void DrawProjectorRoleButton(AreaLitAuditFinding finding, AreaLitProjectorRole role, string label, bool disabled)
        {
            var choice = AreaLitAuditLayers.WithRole(finding, role);
            var reason = FindingReason(choice);
            using (new EditorGUI.DisabledScope(disabled || reason != null))
                if (GUILayout.Button(new GUIContent(label, reason ?? AreaLitAuditFixes.ChangeDescription(choice)), GUILayout.ExpandWidth(false)))
                    ApplyFix(choice);
        }

        // Operations return an actionable warning, or null. Successful changes need no banner.
        private void RunOperation(Func<string> operation)
        {
            try
            {
                var blocked = AreaLitAudit.BlockedReason;
                if (blocked != null) throw new InvalidOperationException(blocked);
                var warning = operation();
                if (RunAudit())
                {
                    status = warning;
                    statusSeverity = MessageType.Warning;
                }
                else
                    status = "Change applied, but the audit could not refresh: " + status +
                        (string.IsNullOrEmpty(warning) ? "" : "\n" + warning);
            }
            catch (ExitGUIException) { throw; }
            catch (Exception exception)
            {
                status = "The operation did not complete: " + exception.Message;
                statusSeverity = MessageType.Error;
                stale = true;
                Debug.LogException(exception);
            }
            GUIUtility.ExitGUI();
        }

        private bool Foldout(string key, string label, bool bold = false, string tooltip = null)
        {
            // Use the actual allocated width for wrapping and height, not an Inspector label width
            // or a window-width estimate. Keep actions out of this full-width title row.
            var style = new GUIStyle(EditorStyles.foldout)
            {
                wordWrap = true, fixedHeight = 0, stretchWidth = true, margin = new RectOffset(),
                fontStyle = bold ? FontStyle.Bold : FontStyle.Normal
            };
            var content = new GUIContent(label, tooltip);
            var rectangle = GUILayoutUtility.GetRect(content, style, GUILayout.ExpandWidth(true));
            // Unity 2022.3 draws Foldout text at labelWidth even when given a wider rectangle.
            var previousLabelWidth = EditorGUIUtility.labelWidth;
            bool open;
            try
            {
                EditorGUIUtility.labelWidth = rectangle.width;
                open = EditorGUI.Foldout(rectangle, expanded.Contains(key), content, true, style);
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }
            if (open) expanded.Add(key); else expanded.Remove(key);
            return open;
        }

        private static void DrawObject(UnityEngine.Object target)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField(target, typeof(UnityEngine.Object), true);
                using (new EditorGUI.DisabledScope(target == null))
                    if (GUILayout.Button("Select", GUILayout.Width(60)))
                    {
                        Selection.activeObject = target;
                        EditorGUIUtility.PingObject(target);
                    }
            }
            GUILayout.Label(AreaLitAudit.Describe(target), EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawBindingActions(AreaLitAuditBindingGroup group, bool disabled)
        {
            Texture chosen;
            if (!chosenTextures.TryGetValue(group.property, out chosen))
            {
                var candidates = group.AssignedTextures;
                chosen = candidates.Length == 1 ? candidates[0] : null;
                chosenTextures[group.property] = chosen;
            }
            var previousChoice = chosen;
            chosen = (Texture)EditorGUILayout.ObjectField("Texture", chosen, typeof(Texture), false);
            if (chosen != previousChoice) preflight.Clear();
            chosenTextures[group.property] = chosen;
            var applyReason = BindingReason(group, chosen, false);
            var removeReason = BindingReason(group, null, true);
            var repairBlocked = AreaLitOcclusionUvRepair.HasIncompleteRepair;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(disabled || repairBlocked || applyReason != null))
                    if (GUILayout.Button(new GUIContent("Apply to all…", applyReason))) ApplyBindings(group, chosen, false);
                using (new EditorGUI.DisabledScope(disabled || repairBlocked || removeReason != null))
                    if (GUILayout.Button(new GUIContent("Clear all…", removeReason))) ApplyBindings(group, null, true);
            }
            if (repairBlocked) GUILayout.Label("Recover the interrupted UV repair first.", EditorStyles.wordWrappedMiniLabel);
            else if (chosen == null) GUILayout.Label("Choose a texture to apply, or clear the slot.", EditorStyles.wordWrappedMiniLabel);
            if (group.excludedRecursiveMaterials > 0)
                GUILayout.Label(group.excludedRecursiveMaterials + " recursive emitter inputs excluded.", EditorStyles.wordWrappedMiniLabel);
            DrawBindingMembers(group);
        }

        private void DrawBindingMembers(AreaLitAuditBindingGroup group)
        {
            if (!Foldout("binding-members:" + group.property, "Materials (" + group.members.Count + ")")) return;
            foreach (var member in group.members)
            {
                DrawObject(member.material);
                GUILayout.Label("Current: " + (member.texture == null ? "Unassigned" : AreaLitAudit.Describe(member.texture)), EditorStyles.wordWrappedMiniLabel);
                GUILayout.Space(4);
            }
        }

        private void ApplyBindings(AreaLitAuditBindingGroup group, Texture texture, bool clear)
        {
            var action = clear ? "Clear " + group.label : "Apply " + AreaLitAudit.Describe(texture) + " as " + group.label;
            if (!EditorUtility.DisplayDialog("Change shared AreaLit materials?", action + " on all " + group.members.Count +
                " listed materials?\n\n" + (clear && group.required ? "This is a required slot. Clearing it will produce audit errors.\n\n" : "") +
                "Texture references only; tiling, offset and shader controls stay unchanged. Shared materials affect other scenes too. " +
                (group.excludedRecursiveMaterials > 0 ? "Recursive emitter inputs are excluded. " : "") +
                "Undo is available; nothing is saved.", clear ? "Clear all" : "Apply to all", "Cancel")) return;
            RunOperation(() =>
            {
                AreaLitAuditBindings.Apply(group, texture, clear, includeDisabled);
                return null;
            });
        }

        private string BindingReason(AreaLitAuditBindingGroup group, Texture texture, bool clear)
        {
            // Hundreds of materials must not cause filesystem/version-control checks every repaint.
            // The actual operation independently rescans and revalidates after confirmation.
            var key = "binding:" + group.property + ":" + clear + ":" + (texture == null ? 0 : texture.GetInstanceID());
            string reason;
            if (!preflight.TryGetValue(key, out reason))
            {
                reason = AreaLitAuditBindings.UnavailableReason(group, texture, clear);
                preflight[key] = reason;
            }
            return reason;
        }

        private string FindingReason(AreaLitAuditFinding finding)
        {
            if (finding.layerChange == null) return AreaLitAuditFixes.UnavailableReason(finding);
            // Grouped emitter checks may cover hundreds of objects. Revalidate again when applying,
            // not on every repaint. Role choices have separate entries in the same snapshot cache.
            var key = "layers:" + finding.id + ":" + finding.layerChange.desiredRole;
            if (!preflight.TryGetValue(key, out var reason))
            {
                reason = AreaLitAuditFixes.UnavailableReason(finding);
                preflight[key] = reason;
            }
            return reason;
        }

        private void DrawAssignmentTool(bool disabled)
        {
            if (!report.bindings.Any(group => group.members.Count > 0)) return;
            GUILayout.Label("Material assignments", EditorStyles.boldLabel);
            var current = report.bindings.FindIndex(group => group.property == assignmentProperty);
            var selected = EditorGUILayout.Popup("Slot", Mathf.Max(0, current),
                report.bindings.Select(group => group.label + (group.required ? " (required)" : "")).ToArray());
            var group = report.bindings[selected];
            assignmentProperty = group.property;
            GUILayout.Label(group.Summary, EditorStyles.wordWrappedMiniLabel);
            DrawBindingActions(group, disabled);
            GUILayout.Space(8);
        }
    }
}
