using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.AreaLitOcclusion
{
    internal sealed partial class AreaLitAuditWindow
    {
        private void DrawOcclusionTools(bool disabled)
        {
            GUILayout.Space(12);
            if (!Foldout("occlusion-uv-tools", "Occlusion tiling / offset tools")) return;
            GUILayout.Label("Applies to scanned mesh renderers with AreaLit occlusion maps.", EditorStyles.wordWrappedLabel);
            var reason = AreaLitOcclusionUvRepair.GetOperationBlockReason();
            using (new EditorGUI.DisabledScope(disabled || !string.IsNullOrEmpty(reason) || report.uvConflicts.eligibleAssignments == 0))
            {
                if (GUILayout.Button("Match lightmap tiling / offset (UV1)…")) ApplyUvOperation(OcclusionUvOperation.MatchRendererLightmap);
                if (GUILayout.Button("Reset tiling to 1,1 and offset to 0,0…")) ApplyUvOperation(OcclusionUvOperation.ResetToDefault);
            }
            if (!string.IsNullOrEmpty(reason)) GUILayout.Label(reason, EditorStyles.wordWrappedMiniLabel);
            else if (report.uvConflicts.eligibleAssignments == 0)
                GUILayout.Label("No eligible occlusion-map assignments in this scope.", EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawUvConflictFinding(bool disabled)
        {
            var conflicts = report.uvConflicts;
            var reason = AreaLitOcclusionUvRepair.GetOperationBlockReason();
            if (string.IsNullOrEmpty(reason))
                reason = conflicts.conflicts.Select(AreaLitOcclusionUvRepair.GetConflictRepairBlockReason).FirstOrDefault(value => !string.IsNullOrEmpty(value));
            using (new EditorGUI.DisabledScope(disabled || !string.IsNullOrEmpty(reason)))
                if (GUILayout.Button("Fix all UV conflicts (" + conflicts.RequiredVariantCount + " variants)…")) ApplyUvRepair(null);
            if (!string.IsNullOrEmpty(reason)) GUILayout.Label(reason, EditorStyles.wordWrappedMiniLabel);
            if (!Foldout("uv-conflict-materials", "Conflicting materials (" + conflicts.conflicts.Count + ")")) return;
            foreach (var conflict in conflicts.conflicts)
            {
                GUILayout.Space(6);
                if (!Foldout("uv-material:" + conflict.stableKey, (conflict.material == null ? "Missing material" : conflict.material.name) + " — " + conflict.groups.Count + " mappings, " + conflict.RendererCount + " renderers")) continue;
                DrawObject(conflict.material);
                foreach (var group in conflict.groups)
                {
                    if (!Foldout("uv-mapping:" + conflict.stableKey + ":" + group.StableKey,
                        "Tiling " + group.scaleOffset.x + ", " + group.scaleOffset.y + " / Offset " + group.scaleOffset.z + ", " + group.scaleOffset.w +
                        " — " + group.rendererUses.Count + " renderers")) continue;
                    foreach (var use in group.rendererUses)
                    {
                        DrawObject(use.renderer);
                        GUILayout.Label("Material slots: " + string.Join(", ", use.materialSlots), EditorStyles.wordWrappedMiniLabel);
                    }
                }
                var singleReason = AreaLitOcclusionUvRepair.GetOperationBlockReason();
                if (string.IsNullOrEmpty(singleReason)) singleReason = AreaLitOcclusionUvRepair.GetConflictRepairBlockReason(conflict);
                using (new EditorGUI.DisabledScope(disabled || !string.IsNullOrEmpty(singleReason)))
                    if (GUILayout.Button("Fix this material (" + conflict.groups.Count + " variants)…")) ApplyUvRepair(conflict.stableKey);
                if (!string.IsNullOrEmpty(singleReason)) GUILayout.Label(singleReason, EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void DrawUvRepairJournal(bool disabled)
        {
            if (!AreaLitOcclusionUvRepair.HasRepairJournal) return;
            GUILayout.Space(8);
            EditorGUILayout.HelpBox(AreaLitOcclusionUvRepair.GetJournalSummary(),
                AreaLitOcclusionUvRepair.HasIncompleteRepair ? MessageType.Error : MessageType.Info);
            var reason = AreaLitOcclusionUvRepair.GetRevertBlockReason();
            using (new EditorGUI.DisabledScope(disabled || !string.IsNullOrEmpty(reason)))
                if (GUILayout.Button(AreaLitOcclusionUvRepair.HasIncompleteRepair ? "Revert interrupted UV repair…" : "Revert UV material repairs…"))
                {
                    if (EditorUtility.DisplayDialog("Revert tracked UV repairs?",
                        "Restore the tracked renderer material assignments and remove generated variants only when unreferenced. Scenes are not saved automatically.",
                        "Revert repairs", "Cancel"))
                        RunOperation(() =>
                        {
                            var result = AreaLitOcclusionUvRepair.RevertTrackedRepair();
                            return result.warnings.Count > 0 ? "Some UV repairs could not be reverted. See the Console for details." : null;
                        });
                }
            if (!string.IsNullOrEmpty(reason)) GUILayout.Label(reason, EditorStyles.wordWrappedMiniLabel);
        }

        private void ApplyUvOperation(OcclusionUvOperation operation)
        {
            var match = operation == OcclusionUvOperation.MatchRendererLightmap;
            if (!EditorUtility.DisplayDialog("Change AreaLit occlusion coordinates?",
                (match ? "Copy renderer lightmap tiling / offset and select UV1. Materials with conflicting mappings are skipped; use the UV conflict repair first."
                    : "Reset occlusion tiling to 1,1 and offset to 0,0, preserving the UV channel.") +
                "\n\nOnly eligible materials in the audit scope are considered. Shared assets affect other scenes too. Undo is available; nothing is auto-saved.",
                match ? "Match coordinates" : "Reset coordinates", "Cancel")) return;
            RunOperation(() =>
            {
                var result = AreaLitOcclusionUvTools.Apply(operation, includeDisabled);
                return result.warnings.Count > 0 ? "Some materials were skipped. See the Console for details." : null;
            });
        }

        private void ApplyUvRepair(string materialKey)
        {
            var conflicts = report.uvConflicts.conflicts.Where(item => materialKey == null || item.stableKey == materialKey).ToList();
            var snapshot = UvSnapshot(report.uvConflicts, materialKey);
            if (!EditorUtility.DisplayDialog("Create occlusion material variants?",
                "Create or reuse " + conflicts.Sum(item => item.groups.Count) + " variants for " + conflicts.Count +
                " conflicting material(s), then reassign " + conflicts.Sum(item => item.AssignmentCount) +
                " renderer slots.\n\nVariants are saved under Assets/Lightbulb/AreaLitOcclusion/Generated. Original materials stay unchanged. " +
                "Scene assignments support Undo; tracked revert also cleans up unreferenced variants. Scenes are not auto-saved.",
                "Create variants and reassign", "Cancel")) return;
            RunOperation(() =>
            {
                if (snapshot != UvSnapshot(AreaLitOcclusionUvTools.ScanConflicts(includeDisabled), materialKey))
                    throw new InvalidOperationException("The UV conflict scope changed. Run the audit again.");
                var result = materialKey == null ? AreaLitOcclusionUvRepair.RepairAll(includeDisabled) :
                    AreaLitOcclusionUvRepair.RepairMaterial(materialKey, includeDisabled);
                return result.warnings.Count > 0 ? "Some UV repairs need attention. See the Console for details." : null;
            });
        }

        private static string UvSnapshot(OcclusionUvConflictReport conflicts, string materialKey)
        {
            return string.Join("|", conflicts.conflicts.Where(item => materialKey == null || item.stableKey == materialKey)
                .SelectMany(item => item.groups.SelectMany(group => group.rendererUses.Select(use =>
                    item.stableKey + ":" + group.StableKey + ":" + use.renderer.GetInstanceID() + ":" + string.Join(",", use.materialSlots))))
                .OrderBy(value => value, StringComparer.Ordinal));
        }
    }
}
