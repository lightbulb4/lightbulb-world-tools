using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.AreaLitOcclusion
{
    internal static class MochieAreaLitHierarchy
    {
        internal enum Toggle { Keep, Enable, Disable }
        internal enum Assignment { Keep, Assign, Clear }

        [Serializable]
        internal struct SlotEdit
        {
            public Assignment action;
            public Texture texture;
        }

        internal const string ToggleProperty = "_AreaLitToggle";
        internal const string Keyword = "_AREALIT_ON";
        private const string UndoName = "Configure Mochie AreaLit";

        internal static bool IsHierarchyRoot(GameObject root) =>
            root != null && root.scene.IsValid() && root.scene.isLoaded && !EditorUtility.IsPersistent(root);

        internal static Material[] Collect(GameObject root)
        {
            if (!IsHierarchyRoot(root)) return Array.Empty<Material>();
            return root.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials)
                .Concat(root.GetComponentsInChildren<Projector>(true).Select(p => p.material))
                .Where(m => m != null && m.shader != null && m.shader.name.StartsWith("Mochie/", StringComparison.Ordinal)
                    && m.HasProperty(ToggleProperty) && m.HasProperty("_LightMesh") && m.HasProperty("_LightTex0"))
                .Distinct().OrderBy(m => m.name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static string InvalidAssignment(SlotEdit[] edits)
        {
            for (int i = 0; i < edits.Length; i++)
            {
                if (edits[i].action != Assignment.Assign) continue;
                var slot = AreaLitAuditBindings.ReceiverSlots[i];
                var reason = AreaLitAuditBindings.TextureProblem(edits[i].texture, AreaLitAuditBindings.Dimension(slot.property));
                if (reason != null) return slot.label + ": " + reason;
            }
            return null;
        }

        internal static int Apply(GameObject root, Toggle toggle, SlotEdit[] edits, out List<string> skipped)
        {
            skipped = new List<string>();
            if (!IsHierarchyRoot(root)) throw new InvalidOperationException("Choose a hierarchy object.");
            var reason = AreaLitAudit.BlockedReason ?? InvalidAssignment(edits);
            if (reason != null) throw new InvalidOperationException(reason);
            if (AreaLitOcclusionUvRepair.HasIncompleteRepair)
                throw new InvalidOperationException("Recover the interrupted UV material repair before editing shared materials.");

            var changes = new List<(Material material, bool changeToggle, List<(string property, Texture texture)> slots)>();
            bool enable = toggle == Toggle.Enable;
            foreach (var material in Collect(root))
            {
                bool changeToggle = toggle != Toggle.Keep &&
                    (material.GetInt(ToggleProperty) != (enable ? 1 : 0) || material.IsKeywordEnabled(Keyword) != enable);
                var slots = new List<(string property, Texture texture)>();
                for (int i = 0; i < edits.Length; i++)
                {
                    string property = AreaLitAuditBindings.ReceiverSlots[i].property;
                    if (edits[i].action == Assignment.Keep || !material.HasProperty(property)) continue;
                    Texture texture = edits[i].action == Assignment.Clear ? null : edits[i].texture;
                    if (material.GetTexture(property) != texture) slots.Add((property, texture));
                }
                if (!changeToggle && slots.Count == 0) continue;

                reason = AreaLitAuditFixes.AssetWriteBlockReason(material);
                if (reason == null && material.isVariant &&
                    ((changeToggle && material.IsPropertyLockedByAncestor(ToggleProperty)) ||
                     slots.Any(slot => material.IsPropertyLockedByAncestor(slot.property))))
                    reason = "A requested property is locked by a parent material.";
                if (reason != null) skipped.Add(AreaLitAudit.Describe(material) + ": " + reason);
                else changes.Add((material, changeToggle, slots));
            }
            if (changes.Count == 0) return 0;

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            Undo.RegisterCompleteObjectUndo(changes.Select(c => (UnityEngine.Object)c.material).ToArray(), UndoName);
            try
            {
                foreach (var change in changes)
                {
                    var material = change.material;
                    if (change.changeToggle)
                    {
                        material.SetInt(ToggleProperty, enable ? 1 : 0);
                        if (enable) material.EnableKeyword(Keyword);
                        else material.DisableKeyword(Keyword);
                    }
                    foreach (var slot in change.slots) material.SetTexture(slot.property, slot.texture);
                    EditorUtility.SetDirty(material);
                }
                Undo.CollapseUndoOperations(group);
                Undo.IncrementCurrentGroup();
            }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                throw;
            }
            return changes.Count;
        }
    }
}
