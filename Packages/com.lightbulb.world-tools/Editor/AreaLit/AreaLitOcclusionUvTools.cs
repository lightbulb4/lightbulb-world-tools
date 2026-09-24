using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.AreaLitOcclusion
{
    internal enum OcclusionUvOperation
    {
        MatchRendererLightmap,
        ResetToDefault
    }

    internal sealed class OcclusionUvConflictRendererUse
    {
        public MeshRenderer renderer;
        public string hierarchyPath;
        public readonly List<int> materialSlots = new List<int>();

        public int AssignmentCount
        {
            get { return materialSlots.Count; }
        }
    }

    internal sealed class OcclusionUvConflictGroup
    {
        public Vector4 scaleOffset;
        public readonly List<OcclusionUvConflictRendererUse> rendererUses =
            new List<OcclusionUvConflictRendererUse>();

        public int AssignmentCount
        {
            get { return rendererUses.Sum(use => use.AssignmentCount); }
        }

        public string StableKey
        {
            get
            {
                return scaleOffset.x.ToString("R", CultureInfo.InvariantCulture) + ":" +
                       scaleOffset.y.ToString("R", CultureInfo.InvariantCulture) + ":" +
                       scaleOffset.z.ToString("R", CultureInfo.InvariantCulture) + ":" +
                       scaleOffset.w.ToString("R", CultureInfo.InvariantCulture);
            }
        }
    }

    internal sealed class OcclusionUvMaterialConflict
    {
        public Material material;
        public string assetPath;
        public string stableKey;
        public readonly List<OcclusionUvConflictGroup> groups = new List<OcclusionUvConflictGroup>();

        public int RendererCount
        {
            get { return groups.SelectMany(group => group.rendererUses).Select(use => use.renderer).Distinct().Count(); }
        }

        public int AssignmentCount
        {
            get { return groups.Sum(group => group.AssignmentCount); }
        }
    }

    internal sealed class OcclusionUvConflictReport
    {
        public int eligibleAssignments;
        public int excludedDisabledRenderers;
        public readonly List<OcclusionUvMaterialConflict> conflicts = new List<OcclusionUvMaterialConflict>();

        public int RequiredVariantCount
        {
            get { return conflicts.Sum(conflict => conflict.groups.Count); }
        }

        public int RendererCount
        {
            get
            {
                return conflicts.SelectMany(conflict => conflict.groups)
                    .SelectMany(group => group.rendererUses)
                    .Select(use => use.renderer)
                    .Distinct()
                    .Count();
            }
        }

        public int AssignmentCount
        {
            get { return conflicts.Sum(conflict => conflict.AssignmentCount); }
        }
    }

    internal sealed class OcclusionUvUpdateResult
    {
        public int eligibleAssignments;
        public int excludedDisabledRenderers;
        public int eligibleMaterials;
        public int updatedMaterials;
        public int unchangedMaterials;
        public int skippedMaterials;
        public readonly List<string> warnings = new List<string>();

        public string GetSummary(OcclusionUvOperation operation)
        {
            var action = operation == OcclusionUvOperation.MatchRendererLightmap
                ? "Matched lightmap tiling and offset on "
                : "Reset tiling and offset on ";
            return action + updatedMaterials + " material(s). " +
                   unchangedMaterials + " already matched; " +
                   skippedMaterials + " skipped. " +
                   eligibleAssignments + " eligible renderer assignment(s) were scanned. Undo is available.";
        }
    }

    internal static class AreaLitOcclusionUvTools
    {
        internal const string AreaLitToggleProperty = "_AreaLitToggle";
        internal const string AreaLitOcclusionProperty = "_AreaLitOcclusion";
        internal const string AreaLitOcclusionUvSetProperty = "_AreaLitOcclusionUVSet";
        internal static readonly Vector4 DefaultScaleOffset = new Vector4(1f, 1f, 0f, 0f);

        private sealed class MaterialPlan
        {
            public Material material;
            public readonly List<OcclusionUvConflictGroup> groups = new List<OcclusionUvConflictGroup>();
        }

        public static OcclusionUvConflictReport ScanConflicts(bool includeDisabledObjects)
        {
            EnsureOriginalScenesAreOpen();

            var scanResult = new OcclusionUvUpdateResult();
            var plans = CollectPlans(scanResult, includeDisabledObjects);
            var report = new OcclusionUvConflictReport
            {
                eligibleAssignments = scanResult.eligibleAssignments,
                excludedDisabledRenderers = scanResult.excludedDisabledRenderers
            };
            foreach (var plan in plans.Values
                         .Where(item => item.groups.Count > 1)
                         .OrderBy(item => item.material.name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => AssetDatabase.GetAssetPath(item.material), StringComparer.OrdinalIgnoreCase))
            {
                var conflict = new OcclusionUvMaterialConflict
                {
                    material = plan.material,
                    assetPath = AssetDatabase.GetAssetPath(plan.material),
                    stableKey = GetMaterialStableKey(plan.material)
                };
                conflict.groups.AddRange(plan.groups.OrderBy(group => group.StableKey, StringComparer.Ordinal));
                report.conflicts.Add(conflict);
            }

            return report;
        }

        public static OcclusionUvUpdateResult Apply(OcclusionUvOperation operation, bool includeDisabledObjects)
        {
            EnsureOriginalScenesAreOpen();
            var blocked = AreaLitOcclusionUvRepair.GetOperationBlockReason();
            if (!string.IsNullOrEmpty(blocked)) throw new InvalidOperationException(blocked);

            var result = new OcclusionUvUpdateResult();
            var plans = CollectPlans(result, includeDisabledObjects);
            result.eligibleMaterials = plans.Count;

            var writablePlans = new List<KeyValuePair<MaterialPlan, Vector4>>();
            foreach (var plan in plans.Values.OrderBy(item => item.material.name, StringComparer.OrdinalIgnoreCase))
            {
                if (operation == OcclusionUvOperation.MatchRendererLightmap && plan.groups.Count > 1)
                {
                    result.skippedMaterials++;
                    var rendererPaths = plan.groups.SelectMany(group => group.rendererUses)
                        .Select(use => use.hierarchyPath)
                        .Distinct()
                        .ToArray();
                    result.warnings.Add(
                        "Skipped shared material '" + plan.material.name +
                        "' because its renderers use different lightmap tiling/offset values: " +
                        string.Join(", ", rendererPaths.Take(6).ToArray()) +
                        (rendererPaths.Length > 6 ? ", ..." : string.Empty));
                    continue;
                }

                var writableReason = GetMaterialWriteBlockReason(plan.material);
                if (!string.IsNullOrEmpty(writableReason))
                {
                    result.skippedMaterials++;
                    result.warnings.Add("Skipped material '" + plan.material.name + "': " + writableReason);
                    continue;
                }

                var desired = operation == OcclusionUvOperation.ResetToDefault
                    ? DefaultScaleOffset
                    : plan.groups[0].scaleOffset;
                if (MaterialAlreadyMatches(plan.material, desired, operation))
                {
                    result.unchangedMaterials++;
                    continue;
                }

                writablePlans.Add(new KeyValuePair<MaterialPlan, Vector4>(plan, desired));
            }

            if (writablePlans.Count > 0)
            {
                Undo.IncrementCurrentGroup();
                var undoGroup = Undo.GetCurrentGroup();
                var undoName = operation == OcclusionUvOperation.MatchRendererLightmap
                    ? "Match AreaLit Occlusion to Lightmaps"
                    : "Reset AreaLit Occlusion Tiling and Offset";
                Undo.SetCurrentGroupName(undoName);
                Undo.RecordObjects(
                    writablePlans.Select(item => (UnityEngine.Object)item.Key.material).ToArray(),
                    undoName);

                foreach (var item in writablePlans)
                {
                    ApplyScaleOffset(item.Key.material, item.Value, operation == OcclusionUvOperation.MatchRendererLightmap);
                    EditorUtility.SetDirty(item.Key.material);
                    result.updatedMaterials++;
                }

                Undo.CollapseUndoOperations(undoGroup);
                Undo.IncrementCurrentGroup();
            }

            foreach (var warning in result.warnings)
            {
                Debug.LogWarning("[AreaLit Occlusion] " + warning);
            }
            Debug.Log("[AreaLit Occlusion] " + result.GetSummary(operation));
            return result;
        }

        internal static void ApplyScaleOffset(Material material, Vector4 scaleOffset, bool selectLightmapUv)
        {
            material.SetTextureScale(
                AreaLitOcclusionProperty,
                new Vector2(scaleOffset.x, scaleOffset.y));
            material.SetTextureOffset(
                AreaLitOcclusionProperty,
                new Vector2(scaleOffset.z, scaleOffset.w));

            // Lightmap UVs are UV1 before each renderer's atlas transform is applied.
            // The copied texture scale/offset supplies that transform for AreaLit.
            if (selectLightmapUv && material.HasProperty(AreaLitOcclusionUvSetProperty))
            {
                material.SetFloat(AreaLitOcclusionUvSetProperty, 1f);
            }
        }

        internal static bool IsEligibleMaterial(Material material)
        {
            return material != null &&
                   material.shader != null &&
                   material.HasProperty(AreaLitToggleProperty) &&
                   material.GetFloat(AreaLitToggleProperty) > 0.5f &&
                   material.HasProperty(AreaLitOcclusionProperty) &&
                   material.GetTexture(AreaLitOcclusionProperty) != null;
        }

        internal static string GetMaterialStableKey(Material material)
        {
            if (material == null) return string.Empty;
            if (EditorUtility.IsPersistent(material))
            {
                return GlobalObjectId.GetGlobalObjectIdSlow(material).ToString();
            }
            return "instance:" + material.GetInstanceID();
        }

        internal static bool Approximately(Vector4 left, Vector4 right)
        {
            return Mathf.Abs(left.x - right.x) <= 0.00001f &&
                   Mathf.Abs(left.y - right.y) <= 0.00001f &&
                   Mathf.Abs(left.z - right.z) <= 0.00001f &&
                   Mathf.Abs(left.w - right.w) <= 0.00001f;
        }

        private static Dictionary<Material, MaterialPlan> CollectPlans(
            OcclusionUvUpdateResult result,
            bool includeDisabledObjects)
        {
            var plans = new Dictionary<Material, MaterialPlan>();
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!renderer.gameObject.scene.IsValid() || !renderer.gameObject.scene.isLoaded) continue;
                if (AreaLitOcclusionPaths.IsTransactionScene(renderer.gameObject.scene.path)) continue;

                var materials = renderer.sharedMaterials;
                if (!includeDisabledObjects && IsDisabled(renderer))
                {
                    if (materials.Any(IsEligibleMaterial)) result.excludedDisabledRenderers++;
                    continue;
                }

                for (var materialSlot = 0; materialSlot < materials.Length; materialSlot++)
                {
                    var material = materials[materialSlot];
                    if (!IsEligibleMaterial(material)) continue;
                    result.eligibleAssignments++;

                    MaterialPlan plan;
                    if (!plans.TryGetValue(material, out plan))
                    {
                        plan = new MaterialPlan { material = material };
                        plans.Add(material, plan);
                    }

                    var desired = renderer.lightmapScaleOffset;
                    var group = plan.groups.FirstOrDefault(item => Approximately(item.scaleOffset, desired));
                    if (group == null)
                    {
                        group = new OcclusionUvConflictGroup { scaleOffset = desired };
                        plan.groups.Add(group);
                    }

                    var rendererUse = group.rendererUses.FirstOrDefault(item => item.renderer == renderer);
                    if (rendererUse == null)
                    {
                        rendererUse = new OcclusionUvConflictRendererUse
                        {
                            renderer = renderer,
                            hierarchyPath = GetRendererPath(renderer)
                        };
                        group.rendererUses.Add(rendererUse);
                    }
                    rendererUse.materialSlots.Add(materialSlot);
                }
            }

            foreach (var plan in plans.Values)
            {
                foreach (var group in plan.groups)
                {
                    foreach (var use in group.rendererUses) use.materialSlots.Sort();
                    group.rendererUses.Sort((left, right) =>
                        StringComparer.OrdinalIgnoreCase.Compare(left.hierarchyPath, right.hierarchyPath));
                }
            }
            return plans;
        }

        private static bool IsDisabled(MeshRenderer renderer)
        {
            return !renderer.enabled || !renderer.gameObject.activeInHierarchy;
        }

        private static bool MaterialAlreadyMatches(
            Material material,
            Vector4 desired,
            OcclusionUvOperation operation)
        {
            var scale = material.GetTextureScale(AreaLitOcclusionProperty);
            var offset = material.GetTextureOffset(AreaLitOcclusionProperty);
            var current = new Vector4(scale.x, scale.y, offset.x, offset.y);
            if (!Approximately(current, desired)) return false;

            return operation != OcclusionUvOperation.MatchRendererLightmap ||
                   !material.HasProperty(AreaLitOcclusionUvSetProperty) ||
                   Mathf.Approximately(material.GetFloat(AreaLitOcclusionUvSetProperty), 1f);
        }

        private static string GetMaterialWriteBlockReason(Material material)
        {
            if (!EditorUtility.IsPersistent(material)) return string.Empty;

            var assetPath = AssetDatabase.GetAssetPath(material);
            if (string.IsNullOrEmpty(assetPath) ||
                !assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return "the material is outside the writable Assets folder.";
            }

            var absolutePath = AreaLitOcclusionPaths.ToAbsolutePath(assetPath);
            if (File.Exists(absolutePath) && (File.GetAttributes(absolutePath) & FileAttributes.ReadOnly) != 0)
            {
                return "the material file is read-only.";
            }
            return string.Empty;
        }

        private static void EnsureOriginalScenesAreOpen()
        {
            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (scene.isLoaded && AreaLitOcclusionPaths.IsTransactionScene(scene.path))
                {
                    throw new InvalidOperationException(
                        "Open the original scene before changing AreaLit occlusion tiling and offset values.");
                }
            }
        }

        private static string GetRendererPath(Renderer renderer)
        {
            var names = new Stack<string>();
            var current = renderer.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return renderer.gameObject.scene.name + ":" + string.Join("/", names.ToArray());
        }
    }
}
