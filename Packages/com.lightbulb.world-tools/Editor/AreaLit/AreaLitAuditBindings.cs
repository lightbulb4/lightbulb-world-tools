using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lightbulb.AreaLitOcclusion
{
    internal sealed class AreaLitAuditBinding
    {
        public Material material;
        public Texture texture;
    }

    // One scene-wide decision per property, not one warning per material.
    internal sealed class AreaLitAuditBindingGroup
    {
        public string property;
        public string label;
        public bool required;
        public int excludedRecursiveMaterials;
        public TextureDimension dimension;
        public readonly List<AreaLitAuditBinding> members = new List<AreaLitAuditBinding>();

        public int MissingCount => members.Count(item => item.texture == null);
        public Texture[] AssignedTextures => members.Select(item => item.texture).Where(texture => texture != null).Distinct().ToArray();
        public bool HasMixedPresence => MissingCount > 0 && MissingCount < members.Count;
        public bool IsDiscrepant => AssignedTextures.Length > 1 || HasMixedPresence;
        public bool NeedsFinding => IsDiscrepant || (required && MissingCount > 0);
        public string Summary => members.Count + " materials · " + (members.Count - MissingCount) + " assigned · " +
                                 MissingCount + " empty · " + AssignedTextures.Length + " textures";

        public bool MatchesSnapshot(AreaLitAuditBindingGroup other)
        {
            return other != null && property == other.property && members.Count == other.members.Count &&
                   members.All(item => other.members.Any(current => current.material == item.material && current.texture == item.texture));
        }
    }

    internal static class AreaLitAuditBindings
    {
        public static AreaLitAuditBindingGroup Collect(string property, string label, bool required,
            IEnumerable<Material> materials, int excludedRecursiveMaterials = 0)
        {
            var group = new AreaLitAuditBindingGroup
            {
                property = property, label = label, required = required,
                dimension = property == "_LightTex3" ? TextureDimension.Tex2DArray : TextureDimension.Tex2D,
                excludedRecursiveMaterials = excludedRecursiveMaterials
            };
            foreach (var material in materials.Where(material => material.HasProperty(property)).Distinct()
                         .OrderBy(AreaLitAudit.Describe, StringComparer.OrdinalIgnoreCase))
                group.members.Add(new AreaLitAuditBinding { material = material, texture = material.GetTexture(property) });
            return group;
        }

        public static void AddFindings(AreaLitAuditReport report)
        {
            foreach (var group in report.bindings.Where(group => group.members.Count > 0 && group.NeedsFinding))
            {
                var title = group.MissingCount > 0 ? group.label + " is missing on " + group.MissingCount + " materials" : group.label + " assignments differ";
                var error = group.required || group.HasMixedPresence;
                report.findings.Add(new AreaLitAuditFinding
                {
                    id = "bindings:" + group.property, title = title, summary = group.Summary, detail = group.Summary, bindings = group,
                    severity = error ? MessageType.Error : MessageType.Warning,
                    needsDecision = !error,
                    recommendation = group.required
                        ? "Required on AreaLit-enabled receivers. Use Material assignments to choose one shared texture."
                        : "This may be intentional. Use Material assignments to apply one texture to all or clear the slot."
                });
            }
        }

        public static string UnavailableReason(AreaLitAuditBindingGroup group, Texture texture, bool clear)
        {
            if (group == null || group.members.Count == 0) return "No supported materials are in this scope.";
            if (!clear)
            {
                if (texture == null) return "Choose the texture to apply.";
                if (!EditorUtility.IsPersistent(texture)) return "Choose a saved texture asset, not a temporary texture.";
                if (texture.dimension != group.dimension) return "This slot requires " + group.dimension + ".";
            }
            var desired = clear ? null : texture;
            var changed = group.members.Where(item => item.texture != desired).ToList();
            if (changed.Count == 0) return "All listed materials already have this assignment.";
            foreach (var item in changed)
            {
                var reason = AreaLitAuditFixes.AssetWriteBlockReason(item.material);
                if (reason != null) return AreaLitAudit.Describe(item.material) + ": " + reason;
            }
            return null;
        }

        public static void Apply(AreaLitAuditBindingGroup requested, Texture texture, bool clear, bool includeDisabled)
        {
            var fresh = AreaLitAudit.Scan(includeDisabled).bindings.FirstOrDefault(group => group.property == requested.property);
            if (!requested.MatchesSnapshot(fresh))
                throw new InvalidOperationException("The material list or assignments changed. Run the audit again before applying to all.");
            var reason = UnavailableReason(fresh, texture, clear);
            if (reason != null) throw new InvalidOperationException(reason);
            if (AreaLitOcclusionUvRepair.HasIncompleteRepair)
                throw new InvalidOperationException("Recover the interrupted UV material repair before changing shared material assignments.");

            var desired = clear ? null : texture;
            var materials = fresh.members.Where(item => item.texture != desired).Select(item => item.material).ToArray();
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            var name = (clear ? "Remove " : "Assign ") + fresh.label + " on AreaLit materials";
            Undo.SetCurrentGroupName(name);
            Undo.RegisterCompleteObjectUndo(materials.Cast<UnityEngine.Object>().ToArray(), name);
            try
            {
                foreach (var material in materials)
                {
                    material.SetTexture(fresh.property, desired);
                    EditorUtility.SetDirty(material);
                }
                Undo.CollapseUndoOperations(undoGroup);
                Undo.IncrementCurrentGroup();
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
        }
    }
}
