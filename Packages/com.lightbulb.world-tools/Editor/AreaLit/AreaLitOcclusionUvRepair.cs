using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.AreaLitOcclusion
{
    [Serializable]
    internal sealed class OcclusionUvRepairVariantRecord
    {
        public string assetPath;
        public string sourceMaterialGlobalId;
        public Vector4 scaleOffset;
        public bool createdByRepair;
    }

    [Serializable]
    internal sealed class OcclusionUvRepairAssignmentRecord
    {
        public string rendererGlobalId;
        public ObjectLocator rendererLocator;
        public int materialSlot;
        public string originalMaterialGlobalId;
        public string variantAssetPath;
    }

    [Serializable]
    internal sealed class OcclusionUvRepairJournal
    {
        public int version = 1;
        public string repairId;
        public string createdUtc;
        public string state;
        public string error;
        public List<OcclusionUvRepairVariantRecord> variants = new List<OcclusionUvRepairVariantRecord>();
        public List<OcclusionUvRepairAssignmentRecord> assignments =
            new List<OcclusionUvRepairAssignmentRecord>();
    }

    internal sealed class OcclusionUvRepairResult
    {
        public int createdVariants;
        public int reusedVariants;
        public int reassignedSlots;
        public int restoredSlots;
        public int deletedVariants;
        public int retainedVariants;
        public readonly List<string> warnings = new List<string>();

        public string GetApplySummary()
        {
            return "Resolved shared-material UV conflicts by creating " + createdVariants +
                   " variant(s), reusing " + reusedVariants + " existing variant(s), and reassigning " +
                   reassignedSlots + " renderer material slot(s). Undo and crash-safe revert are available.";
        }

        public string GetRevertSummary()
        {
            return "Restored " + restoredSlots + " renderer material slot(s). Removed " + deletedVariants +
                   " unused generated variant(s); retained " + retainedVariants +
                   " variant(s) that are still referenced.";
        }
    }

    internal static class AreaLitOcclusionUvRepair
    {
        private const string GeneratedVariantLabel = "AreaLitOcclusionUvVariant";
        private const string GeneratedVariantFolderName = "UV Material Variants";

        private sealed class PendingVariant
        {
            public Material sourceMaterial;
            public OcclusionUvConflictGroup group;
            public OcclusionUvRepairVariantRecord record;
            public Material variantMaterial;
        }

        private sealed class PendingAssignment
        {
            public MeshRenderer renderer;
            public OcclusionUvRepairAssignmentRecord record;
            public Material variantMaterial;
        }

        private static string LibraryRoot
        {
            get { return Path.Combine(AreaLitOcclusionPaths.ProjectRoot, "Library", "AreaLitOcclusion"); }
        }

        private static string ActiveJournalPath
        {
            get { return Path.Combine(LibraryRoot, "uv-material-repair.json"); }
        }

        private static string GeneratedVariantAssetPath
        {
            get { return AreaLitOcclusionPaths.GeneratedAssetPath + "/" + GeneratedVariantFolderName; }
        }

        public static bool HasRepairJournal
        {
            get { return File.Exists(ActiveJournalPath); }
        }

        public static bool HasIncompleteRepair
        {
            get
            {
                var journal = LoadJournal(false);
                return HasRepairJournal &&
                       (journal == null || !string.Equals(journal.state, "Applied", StringComparison.Ordinal));
            }
        }

        public static string GetJournalSummary()
        {
            var journal = LoadJournal(false);
            if (journal == null)
            {
                return HasRepairJournal
                    ? "The UV material repair journal is unreadable. It was left in place; see the Console for its path."
                    : string.Empty;
            }
            if (!string.Equals(journal.state, "Applied", StringComparison.Ordinal))
            {
                return "An interrupted UV material repair needs recovery before another repair can start.";
            }

            return journal.assignments.Count + " renderer material slot assignment(s) and " +
                   journal.variants.Count(variant => variant.createdByRepair) +
                   " generated variant(s) are tracked for revert.";
        }

        public static string GetConflictRepairBlockReason(OcclusionUvMaterialConflict conflict)
        {
            if (conflict == null || conflict.material == null) return "The source material is missing.";
            if (!EditorUtility.IsPersistent(conflict.material))
            {
                return "The source material must be saved as an asset before it can be duplicated safely.";
            }

            long localId;
            string guid;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(conflict.material, out guid, out localId) ||
                string.IsNullOrEmpty(guid))
            {
                return "The source material does not have a stable Unity asset identity.";
            }

            foreach (var group in conflict.groups)
            {
                if (!IsFinite(group.scaleOffset))
                {
                    return "One required tiling/offset value contains an invalid number.";
                }

                foreach (var use in group.rendererUses)
                {
                    var renderer = use.renderer;
                    if (renderer == null) return "One renderer no longer exists. Rescan the scene.";
                    var scene = renderer.gameObject.scene;
                    if (!scene.IsValid() || !scene.isLoaded) return "One renderer's scene is no longer loaded.";
                    if (string.IsNullOrEmpty(scene.path) || string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(scene.path)))
                    {
                        return "Save every affected scene before creating material variants.";
                    }

                    var materials = renderer.sharedMaterials;
                    foreach (var slot in use.materialSlots)
                    {
                        if (slot < 0 || slot >= materials.Length || materials[slot] != conflict.material)
                        {
                            return "A renderer material assignment changed after the scan. Rescan the scene.";
                        }
                    }
                }
            }
            return string.Empty;
        }

        public static string GetOperationBlockReason()
        {
            var environmentBlockReason = GetEnvironmentBlockReason();
            if (!string.IsNullOrEmpty(environmentBlockReason)) return environmentBlockReason;

            var journal = LoadJournal(false);
            if (journal == null && HasRepairJournal)
            {
                return "The UV material repair journal is unreadable. Resolve or remove it before another repair.";
            }
            if (journal != null && !string.Equals(journal.state, "Applied", StringComparison.Ordinal))
            {
                return "Recover the interrupted UV material repair first.";
            }
            return string.Empty;
        }

        public static string GetRevertBlockReason()
        {
            var environmentBlockReason = GetEnvironmentBlockReason();
            if (!string.IsNullOrEmpty(environmentBlockReason)) return environmentBlockReason;

            var journal = LoadJournal(false);
            if (journal == null)
            {
                return HasRepairJournal
                    ? "The UV material repair journal is unreadable; see the Console for its path."
                    : "There is no tracked UV material repair to revert.";
            }
            foreach (var scenePath in journal.assignments
                         .Select(GetRendererScenePath)
                         .Where(path => !string.IsNullOrEmpty(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var scene = SceneManager.GetSceneByPath(scenePath);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    return "Load every scene affected by the repair before reverting: " + scenePath;
                }
            }
            return string.Empty;
        }

        private static string GetEnvironmentBlockReason()
        {
            if (AreaLitOcclusionBakeController.IsRunning || AreaLitOcclusionJournalStore.HasActiveJournal)
            {
                return "Finish or revert the active bake transaction first.";
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Exit Play Mode before changing scene material assignments.";
            }
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                return "Close Prefab Mode before repairing loaded-scene material assignments.";
            }

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && AreaLitOcclusionPaths.IsTransactionScene(scene.path))
                {
                    return "Open the original scene before repairing material assignments.";
                }
            }
            return string.Empty;
        }

        public static OcclusionUvRepairResult RepairAll(bool includeDisabledObjects)
        {
            return Repair(null, includeDisabledObjects);
        }

        public static OcclusionUvRepairResult RepairMaterial(
            string materialStableKey,
            bool includeDisabledObjects)
        {
            if (string.IsNullOrEmpty(materialStableKey))
            {
                throw new ArgumentException("A material identity is required.", "materialStableKey");
            }
            return Repair(
                new HashSet<string>(new[] { materialStableKey }, StringComparer.Ordinal),
                includeDisabledObjects);
        }

        public static OcclusionUvRepairResult RevertTrackedRepair()
        {
            var blockReason = GetRevertBlockReason();
            if (!string.IsNullOrEmpty(blockReason)) throw new InvalidOperationException(blockReason);

            var journal = LoadJournal(true);
            if (journal == null) throw new InvalidOperationException("There is no tracked UV material repair to revert.");

            var result = new OcclusionUvRepairResult();
            try
            {
                journal.state = "Restoring";
                journal.error = string.Empty;
                SaveJournal(journal);

                RestoreAssignments(journal.assignments, result, "Revert AreaLit UV Material Repair");
                DeleteUnreferencedVariants(journal.variants, result);

                journal.state = result.warnings.Count == 0 ? "Reverted" : "RevertedWithWarnings";
                ArchiveJournal(journal);
                Debug.Log("[AreaLit Occlusion] " + result.GetRevertSummary());
                foreach (var warning in result.warnings)
                {
                    Debug.LogWarning("[AreaLit Occlusion] " + warning);
                }
                return result;
            }
            catch (Exception exception)
            {
                journal.state = "RecoveryRequired";
                journal.error = exception.ToString();
                SaveJournal(journal);
                throw new InvalidOperationException(
                    "The UV material repair could not be fully reverted. The recovery journal was retained.",
                    exception);
            }
        }

        private static OcclusionUvRepairResult Repair(
            HashSet<string> requestedMaterialKeys,
            bool includeDisabledObjects)
        {
            var blockReason = GetOperationBlockReason();
            if (!string.IsNullOrEmpty(blockReason)) throw new InvalidOperationException(blockReason);

            var freshReport = AreaLitOcclusionUvTools.ScanConflicts(includeDisabledObjects);
            var conflicts = requestedMaterialKeys == null
                ? freshReport.conflicts.ToList()
                : freshReport.conflicts.Where(conflict => requestedMaterialKeys.Contains(conflict.stableKey)).ToList();
            if (conflicts.Count == 0)
            {
                throw new InvalidOperationException(
                    requestedMaterialKeys == null
                        ? "No shared-material tiling/offset conflicts remain."
                        : "That material conflict changed or no longer exists. Rescan and try again.");
            }
            if (requestedMaterialKeys != null && conflicts.Count != requestedMaterialKeys.Count)
            {
                throw new InvalidOperationException("A selected material conflict changed. Rescan and try again.");
            }

            foreach (var conflict in conflicts)
            {
                var conflictBlock = GetConflictRepairBlockReason(conflict);
                if (!string.IsNullOrEmpty(conflictBlock))
                {
                    throw new InvalidOperationException(
                        "Cannot repair shared material '" + conflict.material.name + "': " + conflictBlock);
                }
            }

            AreaLitOcclusionPaths.EnsureAssetFolder(AreaLitOcclusionPaths.RootAssetPath);
            AreaLitOcclusionPaths.EnsureAssetFolder(AreaLitOcclusionPaths.GeneratedAssetPath);
            AreaLitOcclusionPaths.EnsureAssetFolder(GeneratedVariantAssetPath);

            var journal = LoadJournal(true) ?? new OcclusionUvRepairJournal
            {
                repairId = AreaLitOcclusionPaths.CreateTransactionId(),
                createdUtc = DateTime.UtcNow.ToString("o"),
                state = "Applied"
            };
            var originalAssignmentCount = journal.assignments.Count;
            var originalVariantCount = journal.variants.Count;
            var pendingVariants = BuildPendingVariants(conflicts, journal);
            var pendingAssignments = BuildPendingAssignments(conflicts, pendingVariants, journal);
            var result = new OcclusionUvRepairResult();

            try
            {
                journal.state = "Preparing";
                journal.error = string.Empty;
                SaveJournal(journal);

                foreach (var pending in pendingVariants)
                {
                    pending.variantMaterial = CreateOrLoadVariant(pending, result);
                }
                foreach (var pending in pendingVariants)
                {
                    if (pending.variantMaterial != null) AssetDatabase.SaveAssetIfDirty(pending.variantMaterial);
                }

                foreach (var assignment in pendingAssignments)
                {
                    assignment.variantMaterial = pendingVariants
                        .First(item => item.record.assetPath == assignment.record.variantAssetPath)
                        .variantMaterial;
                    ValidateAssignmentStillMatches(assignment);
                }

                journal.state = "Applying";
                SaveJournal(journal);
                ApplyAssignments(pendingAssignments, result);

                journal.state = "Applied";
                SaveJournal(journal);
                Debug.Log("[AreaLit Occlusion] " + result.GetApplySummary());
                return result;
            }
            catch (Exception exception)
            {
                try
                {
                    var failedAssignments = journal.assignments.Skip(originalAssignmentCount).ToList();
                    var rollbackResult = new OcclusionUvRepairResult();
                    RestoreAssignments(failedAssignments, rollbackResult, "Roll Back AreaLit UV Material Repair");

                    var failedVariants = journal.variants.Skip(originalVariantCount).ToList();
                    DeleteUnreferencedVariants(failedVariants, rollbackResult);
                    journal.assignments.RemoveRange(
                        originalAssignmentCount,
                        journal.assignments.Count - originalAssignmentCount);
                    journal.variants.RemoveRange(originalVariantCount, journal.variants.Count - originalVariantCount);

                    if (originalAssignmentCount == 0 && originalVariantCount == 0)
                    {
                        journal.state = "FailedAndRecovered";
                        journal.error = exception.ToString();
                        ArchiveJournal(journal);
                    }
                    else
                    {
                        journal.state = "Applied";
                        journal.error = string.Empty;
                        SaveJournal(journal);
                    }
                }
                catch (Exception recoveryException)
                {
                    journal.state = "RecoveryRequired";
                    journal.error = exception + "\nRecovery failure:\n" + recoveryException;
                    SaveJournal(journal);
                    throw new InvalidOperationException(
                        "The UV material repair failed and could not fully recover. The recovery journal was retained.",
                        recoveryException);
                }

                throw new InvalidOperationException(
                    "The UV material repair failed before completion. Its partial changes were rolled back.",
                    exception);
            }
        }

        private static List<PendingVariant> BuildPendingVariants(
            IEnumerable<OcclusionUvMaterialConflict> conflicts,
            OcclusionUvRepairJournal journal)
        {
            var pending = new List<PendingVariant>();
            foreach (var conflict in conflicts)
            {
                foreach (var group in conflict.groups)
                {
                    var assetPath = GetVariantAssetPath(conflict.material, group.scaleOffset);
                    var existingRecord = journal.variants.FirstOrDefault(record =>
                        string.Equals(record.assetPath, assetPath, StringComparison.OrdinalIgnoreCase));
                    var record = existingRecord ?? new OcclusionUvRepairVariantRecord
                    {
                        assetPath = assetPath,
                        sourceMaterialGlobalId = conflict.stableKey,
                        scaleOffset = group.scaleOffset,
                        createdByRepair = AssetDatabase.LoadMainAssetAtPath(assetPath) == null
                    };
                    if (existingRecord == null) journal.variants.Add(record);
                    pending.Add(new PendingVariant
                    {
                        sourceMaterial = conflict.material,
                        group = group,
                        record = record
                    });
                }
            }
            return pending;
        }

        private static List<PendingAssignment> BuildPendingAssignments(
            IEnumerable<OcclusionUvMaterialConflict> conflicts,
            List<PendingVariant> pendingVariants,
            OcclusionUvRepairJournal journal)
        {
            var pending = new List<PendingAssignment>();
            foreach (var conflict in conflicts)
            {
                foreach (var group in conflict.groups)
                {
                    var variant = pendingVariants.First(item =>
                        item.sourceMaterial == conflict.material && item.group == group);
                    foreach (var use in group.rendererUses)
                    {
                        foreach (var materialSlot in use.materialSlots)
                        {
                            var record = new OcclusionUvRepairAssignmentRecord
                            {
                                rendererGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(use.renderer).ToString(),
                                rendererLocator = AreaLitOcclusionDiscovery.CreateLocator(use.renderer),
                                materialSlot = materialSlot,
                                originalMaterialGlobalId = conflict.stableKey,
                                variantAssetPath = variant.record.assetPath
                            };
                            journal.assignments.Add(record);
                            pending.Add(new PendingAssignment { renderer = use.renderer, record = record });
                        }
                    }
                }
            }
            return pending;
        }

        private static Material CreateOrLoadVariant(PendingVariant pending, OcclusionUvRepairResult result)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(pending.record.assetPath);
            if (existing != null)
            {
                if (!AssetDatabase.GetLabels(existing).Contains(GeneratedVariantLabel) ||
                    !MaterialHasScaleOffset(existing, pending.record.scaleOffset))
                {
                    throw new InvalidOperationException(
                        "The generated material path is occupied by an asset that is not the expected UV variant: " +
                        pending.record.assetPath);
                }
                result.reusedVariants++;
                return existing;
            }

            var clone = new Material(pending.sourceMaterial)
            {
                name = Path.GetFileNameWithoutExtension(pending.record.assetPath)
            };
            try
            {
                AreaLitOcclusionUvTools.ApplyScaleOffset(clone, pending.record.scaleOffset, true);
                AssetDatabase.CreateAsset(clone, pending.record.assetPath);
                AssetDatabase.SetLabels(clone, AssetDatabase.GetLabels(clone)
                    .Concat(new[] { GeneratedVariantLabel })
                    .Distinct()
                    .ToArray());
                result.createdVariants++;
                return clone;
            }
            catch
            {
                if (!EditorUtility.IsPersistent(clone)) UnityEngine.Object.DestroyImmediate(clone);
                throw;
            }
        }

        private static void ValidateAssignmentStillMatches(PendingAssignment assignment)
        {
            if (assignment.renderer == null)
            {
                throw new InvalidOperationException("A renderer was removed during the repair preflight.");
            }
            var materials = assignment.renderer.sharedMaterials;
            var slot = assignment.record.materialSlot;
            if (slot < 0 || slot >= materials.Length ||
                AreaLitOcclusionUvTools.GetMaterialStableKey(materials[slot]) != assignment.record.originalMaterialGlobalId)
            {
                throw new InvalidOperationException(
                    "A renderer material assignment changed during repair: " +
                    AreaLitOcclusionDiscovery.GetDisplayPath(assignment.renderer.transform));
            }
            if (assignment.variantMaterial == null)
            {
                throw new InvalidOperationException("A prepared material variant could not be loaded.");
            }
        }

        private static void ApplyAssignments(List<PendingAssignment> assignments, OcclusionUvRepairResult result)
        {
            var renderers = assignments.Select(item => item.renderer).Distinct().ToArray();
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            const string undoName = "Auto-Fix AreaLit UV Material Conflicts";
            Undo.SetCurrentGroupName(undoName);
            Undo.RecordObjects(renderers.Cast<UnityEngine.Object>().ToArray(), undoName);

            foreach (var rendererGroup in assignments.GroupBy(item => item.renderer))
            {
                var renderer = rendererGroup.Key;
                var materials = renderer.sharedMaterials;
                foreach (var assignment in rendererGroup)
                {
                    materials[assignment.record.materialSlot] = assignment.variantMaterial;
                    result.reassignedSlots++;
                }
                renderer.sharedMaterials = materials;
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                EditorUtility.SetDirty(renderer);
                EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
            }
            Undo.CollapseUndoOperations(undoGroup);
        }

        private static void RestoreAssignments(
            IList<OcclusionUvRepairAssignmentRecord> assignments,
            OcclusionUvRepairResult result,
            string undoName)
        {
            var resolved = assignments.Select(record => new
                {
                    record,
                    renderer = ResolveRenderer(record),
                    original = ResolveGlobalMaterial(record.originalMaterialGlobalId),
                    variant = AssetDatabase.LoadAssetAtPath<Material>(record.variantAssetPath)
                })
                .ToList();

            var renderers = resolved.Where(item => item.renderer != null)
                .Select(item => item.renderer)
                .Distinct()
                .ToArray();
            if (renderers.Length > 0)
            {
                Undo.IncrementCurrentGroup();
                var undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(undoName);
                Undo.RecordObjects(renderers.Cast<UnityEngine.Object>().ToArray(), undoName);

                foreach (var item in resolved.AsEnumerable().Reverse())
                {
                    if (item.renderer == null)
                    {
                        result.warnings.Add("A previously repaired renderer no longer exists; no assignment was restored for it.");
                        continue;
                    }

                    var materials = item.renderer.sharedMaterials;
                    if (item.record.materialSlot < 0 || item.record.materialSlot >= materials.Length)
                    {
                        result.warnings.Add(
                            "A material slot no longer exists on " +
                            AreaLitOcclusionDiscovery.GetDisplayPath(item.renderer.transform) + ".");
                        continue;
                    }

                    var current = materials[item.record.materialSlot];
                    if (current == item.original) continue;
                    if (item.variant == null)
                    {
                        result.warnings.Add(
                            "The generated variant is missing, so the current assignment was left untouched on " +
                            AreaLitOcclusionDiscovery.GetDisplayPath(item.renderer.transform) +
                            " slot " + item.record.materialSlot + ".");
                        continue;
                    }
                    if (current != item.variant)
                    {
                        result.warnings.Add(
                            "Left a user-changed material assignment untouched on " +
                            AreaLitOcclusionDiscovery.GetDisplayPath(item.renderer.transform) +
                            " slot " + item.record.materialSlot + ".");
                        continue;
                    }
                    if (item.original == null)
                    {
                        throw new InvalidOperationException(
                            "The original material could not be resolved for " +
                            AreaLitOcclusionDiscovery.GetDisplayPath(item.renderer.transform) + ".");
                    }

                    materials[item.record.materialSlot] = item.original;
                    item.renderer.sharedMaterials = materials;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(item.renderer);
                    EditorUtility.SetDirty(item.renderer);
                    EditorSceneManager.MarkSceneDirty(item.renderer.gameObject.scene);
                    result.restoredSlots++;
                }
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        private static MeshRenderer ResolveRenderer(OcclusionUvRepairAssignmentRecord record)
        {
            GlobalObjectId globalId;
            if (!string.IsNullOrEmpty(record.rendererGlobalId) &&
                GlobalObjectId.TryParse(record.rendererGlobalId, out globalId))
            {
                var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId) as MeshRenderer;
                if (resolved != null) return resolved;
            }

            if (record.rendererLocator == null || string.IsNullOrEmpty(record.rendererLocator.sourceScenePath)) return null;
            var scene = SceneManager.GetSceneByPath(GetRendererScenePath(record));
            return AreaLitOcclusionDiscovery.ResolveLocator(scene, record.rendererLocator) as MeshRenderer;
        }

        private static string GetRendererScenePath(OcclusionUvRepairAssignmentRecord record)
        {
            GlobalObjectId globalId;
            if (!string.IsNullOrEmpty(record.rendererGlobalId) &&
                GlobalObjectId.TryParse(record.rendererGlobalId, out globalId))
            {
                var currentPath = AssetDatabase.GUIDToAssetPath(globalId.assetGUID.ToString());
                if (!string.IsNullOrEmpty(currentPath)) return currentPath;
            }

            return record.rendererLocator == null ? string.Empty : record.rendererLocator.sourceScenePath;
        }

        private static Material ResolveGlobalMaterial(string globalIdValue)
        {
            GlobalObjectId globalId;
            if (string.IsNullOrEmpty(globalIdValue) || !GlobalObjectId.TryParse(globalIdValue, out globalId)) return null;
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId) as Material;
        }

        private static string GetVariantAssetPath(Material source, Vector4 scaleOffset)
        {
            string sourceGuid;
            long sourceLocalId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out sourceGuid, out sourceLocalId) ||
                string.IsNullOrEmpty(sourceGuid))
            {
                throw new InvalidOperationException("The source material does not have a stable asset identity.");
            }

            var sourcePath = AssetDatabase.GetAssetPath(source);
            var sourceHash = AssetDatabase.GetAssetDependencyHash(sourcePath).ToString();
            if (EditorUtility.IsDirty(source))
            {
                // The dependency hash describes the saved asset. Include the in-memory state when
                // the artist has unsaved material edits so an older generated variant is never reused.
                sourceHash += "|dirty|" + Hash128.Compute(EditorJsonUtility.ToJson(source));
            }
            var coordinateKey = scaleOffset.x.ToString("R", CultureInfo.InvariantCulture) + "|" +
                                scaleOffset.y.ToString("R", CultureInfo.InvariantCulture) + "|" +
                                scaleOffset.z.ToString("R", CultureInfo.InvariantCulture) + "|" +
                                scaleOffset.w.ToString("R", CultureInfo.InvariantCulture);
            var hash = Hash128.Compute(sourceGuid + "|" + sourceLocalId + "|" + sourceHash + "|" + coordinateKey)
                .ToString()
                .Substring(0, 12);
            var sourceName = AreaLitOcclusionPaths.SanitizeFileName(source.name);
            if (sourceName.Length > 64) sourceName = sourceName.Substring(0, 64);
            var path = GeneratedVariantAssetPath + "/" + sourceName + "__AreaLitUV_" + hash + ".mat";
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing == null) return path;

            var existingMaterial = existing as Material;
            if (existingMaterial != null &&
                AssetDatabase.GetLabels(existingMaterial).Contains(GeneratedVariantLabel) &&
                MaterialHasScaleOffset(existingMaterial, scaleOffset))
            {
                return path;
            }
            return AssetDatabase.GenerateUniqueAssetPath(path);
        }

        private static bool MaterialHasScaleOffset(Material material, Vector4 desired)
        {
            if (material == null || !material.HasProperty(AreaLitOcclusionUvTools.AreaLitOcclusionProperty)) return false;
            var scale = material.GetTextureScale(AreaLitOcclusionUvTools.AreaLitOcclusionProperty);
            var offset = material.GetTextureOffset(AreaLitOcclusionUvTools.AreaLitOcclusionProperty);
            var current = new Vector4(scale.x, scale.y, offset.x, offset.y);
            return AreaLitOcclusionUvTools.Approximately(current, desired) &&
                   (!material.HasProperty(AreaLitOcclusionUvTools.AreaLitOcclusionUvSetProperty) ||
                    Mathf.Approximately(
                        material.GetFloat(AreaLitOcclusionUvTools.AreaLitOcclusionUvSetProperty),
                        1f));
        }

        private static bool IsFinite(Vector4 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void DeleteUnreferencedVariants(
            IEnumerable<OcclusionUvRepairVariantRecord> records,
            OcclusionUvRepairResult result)
        {
            var candidates = new HashSet<string>(records
                .Where(record => record.createdByRepair && !string.IsNullOrEmpty(record.assetPath))
                .Select(record => record.assetPath), StringComparer.OrdinalIgnoreCase);
            if (candidates.Count == 0) return;

            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    candidates.Remove(AssetDatabase.GetAssetPath(material));
                }
            }

            var loadedRoots = new List<UnityEngine.Object>();
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                loadedRoots.AddRange(scene.GetRootGameObjects());
            }
            if (loadedRoots.Count > 0)
            {
                foreach (var dependency in EditorUtility.CollectDependencies(loadedRoots.ToArray()))
                {
                    var dependencyPath = AssetDatabase.GetAssetPath(dependency);
                    if (!string.IsNullOrEmpty(dependencyPath)) candidates.Remove(dependencyPath);
                }
            }

            if (candidates.Count > 0)
            {
                foreach (var assetPath in AssetDatabase.GetAllAssetPaths()
                             .Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)))
                {
                    if (candidates.Count == 0) break;
                    if (candidates.Contains(assetPath)) continue;
                    foreach (var dependency in AssetDatabase.GetDependencies(assetPath, false))
                    {
                        candidates.Remove(dependency);
                    }
                }
            }

            var createdPaths = records.Where(record => record.createdByRepair)
                .Select(record => record.assetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var assetPath in createdPaths)
            {
                if (!candidates.Contains(assetPath))
                {
                    result.retainedVariants++;
                    continue;
                }
                if (AssetDatabase.LoadMainAssetAtPath(assetPath) == null) continue;
                if (!AssetDatabase.DeleteAsset(assetPath))
                {
                    result.retainedVariants++;
                    result.warnings.Add("Unity could not remove unused generated variant: " + assetPath);
                    continue;
                }
                result.deletedVariants++;
            }
        }

        private static OcclusionUvRepairJournal LoadJournal(bool throwOnFailure)
        {
            if (!File.Exists(ActiveJournalPath)) return null;
            try
            {
                var journal = JsonUtility.FromJson<OcclusionUvRepairJournal>(File.ReadAllText(ActiveJournalPath));
                if (journal == null || journal.version != 1)
                {
                    throw new InvalidDataException("The UV material repair journal has an unsupported format.");
                }
                return journal;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "[AreaLit Occlusion] The UV material repair journal could not be read. It was left in place at " +
                    ActiveJournalPath + "\n" + exception);
                if (throwOnFailure) throw;
                return null;
            }
        }

        private static void SaveJournal(OcclusionUvRepairJournal journal)
        {
            Directory.CreateDirectory(LibraryRoot);
            var temporaryPath = ActiveJournalPath + ".tmp";
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                File.WriteAllText(
                    temporaryPath,
                    JsonUtility.ToJson(journal, true),
                    new UTF8Encoding(false));
                if (File.Exists(ActiveJournalPath))
                {
                    var previousPath = ActiveJournalPath + ".previous";
                    File.Replace(temporaryPath, ActiveJournalPath, previousPath);
                }
                else
                {
                    File.Move(temporaryPath, ActiveJournalPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static void ArchiveJournal(OcclusionUvRepairJournal journal)
        {
            SaveJournal(journal);
            var historyFolder = Path.Combine(LibraryRoot, "UvRepairHistory");
            Directory.CreateDirectory(historyFolder);
            var historyPath = Path.Combine(historyFolder, journal.repairId + ".json");
            if (File.Exists(historyPath))
            {
                historyPath = Path.Combine(historyFolder, journal.repairId + "-" + DateTime.UtcNow.Ticks + ".json");
            }
            File.Move(ActiveJournalPath, historyPath);
        }
    }

    [InitializeOnLoad]
    internal static class AreaLitOcclusionUvRepairRecoveryNotice
    {
        static AreaLitOcclusionUvRepairRecoveryNotice()
        {
            EditorApplication.delayCall += WarnIfRecoveryIsRequired;
        }

        private static void WarnIfRecoveryIsRequired()
        {
            if (!AreaLitOcclusionUvRepair.HasIncompleteRepair) return;
            Debug.LogError(
                "[AreaLit Occlusion] An interrupted UV material repair was found. Open Tools > Lightbulb > " +
                "AreaLit Configuration Audit and use Revert interrupted UV repair before making another UV repair.");
        }
    }
}
