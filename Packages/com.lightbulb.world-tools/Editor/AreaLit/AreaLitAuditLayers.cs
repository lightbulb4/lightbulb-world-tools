using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lightbulb.AreaLitOcclusion
{
    internal enum AreaLitProjectorRole { Unspecified, Players, Mirror }

    internal sealed class AreaLitAuditLayerChange
    {
        public IReadOnlyDictionary<GameObject, int> originalLayers;
        public Projector projector;
        public int originalIgnoreLayers;
        public AreaLitProjectorRole detectedRole;
        public AreaLitProjectorRole desiredRole;

        public bool NeedsRole => projector != null && desiredRole == AreaLitProjectorRole.Unspecified;

        public bool MatchesSnapshot(AreaLitAuditLayerChange other)
        {
            return other != null && projector == other.projector && originalIgnoreLayers == other.originalIgnoreLayers &&
                   detectedRole == other.detectedRole && originalLayers.Count == other.originalLayers.Count &&
                   originalLayers.All(item => other.originalLayers.TryGetValue(item.Key, out var layer) && layer == item.Value);
        }
    }

    // The project's explicit layer convention, isolated from generic texture/camera audit rules.
    // VRChat roles use its documented built-in layers without requiring an SDK assembly reference.
    internal static class AreaLitAuditLayers
    {
        private const int EmitterLayer = 1;
        private const int PlayerLayer = 9;
        private const int LocalPlayerLayer = 10;
        private const int MirrorLayer = 18;
        private const int PlayerReceivers = (1 << PlayerLayer) | (1 << LocalPlayerLayer);
        private const int MirrorReceivers = 1 << MirrorLayer;
        private static bool ProjectorLayersAvailable => LayerMask.NameToLayer("Player") == PlayerLayer &&
            LayerMask.NameToLayer("PlayerLocal") == LocalPlayerLayer && LayerMask.NameToLayer("MirrorReflection") == MirrorLayer;

        public static void AddFindings(AreaLitAuditReport report, IEnumerable<GameObject> emitters, IEnumerable<Projector> projectors)
        {
            var misplaced = emitters.Distinct().Where(item => item.layer != EmitterLayer)
                .OrderBy(AreaLitAudit.Describe, StringComparer.Ordinal).ToDictionary(item => item, item => item.layer);
            if (misplaced.Count > 0)
            {
                var finding = new AreaLitAuditFinding
                {
                    id = "emitter-layers", title = "Emitters need TransparentFX",
                    target = misplaced.Keys.First(), summary = misplaced.Count + " objects on other layers",
                    detail = string.Join("\n", misplaced.Select(item => AreaLitAudit.Describe(item.Key) + ": " + LayerName(item.Value))),
                    recommendation = "Set these LightMesh objects to TransparentFX. Children and capture-camera masks stay unchanged.",
                    severity = MessageType.Error, fix = AreaLitAuditFix.SceneLayers,
                    layerChange = new AreaLitAuditLayerChange { originalLayers = misplaced }
                };
                finding.relatedObjects.AddRange(misplaced.Keys.Skip(1));
                report.findings.Add(finding);
            }

            foreach (var projector in projectors)
            {
                var role = DetectRole(projector);
                if (ProjectorLayersAvailable && role != AreaLitProjectorRole.Unspecified && projector.gameObject.layer == DesiredLayer(role) &&
                    projector.ignoreLayers == DesiredIgnoreLayers(role)) continue;
                var name = role == AreaLitProjectorRole.Mirror ? "Mirror" : "Player";
                report.findings.Add(new AreaLitAuditFinding
                {
                    id = "projector-layers:" + projector.GetInstanceID(), target = projector,
                    title = role == AreaLitProjectorRole.Unspecified ? "Choose projector role" : name + " projector layers need attention",
                    summary = projector.name + " · " + (role == AreaLitProjectorRole.Unspecified ? "Player or mirror setup?" : ExpectedSetup(role)),
                    detail = "Current object layer: " + LayerName(projector.gameObject.layer) + ". Current affected layers: " +
                             ReceiverNames(projector.ignoreLayers) + ".",
                    recommendation = "The fix sets the object layer and ignores every layer except this role's receivers. " +
                                     "Names, layer and avatar-mask bits suggest a role; choose explicitly when unclear. Other projector settings stay unchanged.",
                    severity = role == AreaLitProjectorRole.Unspecified ? MessageType.Warning : MessageType.Error,
                    needsDecision = role == AreaLitProjectorRole.Unspecified, fix = AreaLitAuditFix.SceneLayers,
                    layerChange = new AreaLitAuditLayerChange
                    {
                        originalLayers = new Dictionary<GameObject, int> { { projector.gameObject, projector.gameObject.layer } },
                        projector = projector, originalIgnoreLayers = projector.ignoreLayers, detectedRole = role, desiredRole = role
                    }
                });
            }
        }

        private static AreaLitProjectorRole DetectRole(Projector projector)
        {
            var name = projector.name;
            var mirrorName = name.IndexOf("mirror", StringComparison.OrdinalIgnoreCase) >= 0;
            var playerName = name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             name.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) >= 0;
            // A role-bearing name survives both a broken layer and a broken ignore mask.
            if (mirrorName != playerName) return mirrorName ? AreaLitProjectorRole.Mirror : AreaLitProjectorRole.Players;
            var layerRole = projector.gameObject.layer == PlayerLayer ? AreaLitProjectorRole.Players :
                projector.gameObject.layer == MirrorLayer ? AreaLitProjectorRole.Mirror : AreaLitProjectorRole.Unspecified;
            var affectsPlayers = (~projector.ignoreLayers & PlayerReceivers) != 0;
            var affectsMirror = (~projector.ignoreLayers & MirrorReceivers) != 0;
            var maskRole = affectsPlayers == affectsMirror ? AreaLitProjectorRole.Unspecified :
                affectsPlayers ? AreaLitProjectorRole.Players : AreaLitProjectorRole.Mirror;
            // A name containing both roles needs agreeing layer/mask evidence. An explicit fix
            // supplies that evidence, so it does not keep prompting for the same role afterward.
            if (mirrorName)
                return layerRole != AreaLitProjectorRole.Unspecified && layerRole == maskRole ? layerRole : AreaLitProjectorRole.Unspecified;
            if (layerRole != AreaLitProjectorRole.Unspecified && maskRole != AreaLitProjectorRole.Unspecified && layerRole != maskRole)
                return AreaLitProjectorRole.Unspecified;
            return layerRole != AreaLitProjectorRole.Unspecified ? layerRole : maskRole;
        }

        private static int DesiredLayer(AreaLitProjectorRole role) =>
            role == AreaLitProjectorRole.Mirror ? MirrorLayer : PlayerLayer;

        private static int DesiredIgnoreLayers(AreaLitProjectorRole role) =>
            ~(role == AreaLitProjectorRole.Mirror ? MirrorReceivers : PlayerReceivers);

        private static string ExpectedSetup(AreaLitProjectorRole role) => role == AreaLitProjectorRole.Mirror
            ? "MirrorReflection layer; affects MirrorReflection only"
            : "Player layer; affects Player + PlayerLocal only";

        private static string LayerName(int layer)
        {
            var name = LayerMask.LayerToName(layer);
            return string.IsNullOrEmpty(name) ? "unnamed (" + layer + ")" : name;
        }

        private static string ReceiverNames(int ignoreLayers) => string.Join(", ", Enumerable.Range(0, 32)
            .Where(layer => (ignoreLayers & (1 << layer)) == 0).Select(LayerName));

        public static AreaLitAuditFinding WithRole(AreaLitAuditFinding finding, AreaLitProjectorRole role)
        {
            if (!finding.layerChange.NeedsRole || role == AreaLitProjectorRole.Unspecified)
                throw new InvalidOperationException("Choose a role only for an unresolved projector.");
            var change = finding.layerChange;
            return new AreaLitAuditFinding
            {
                id = finding.id, title = finding.title, target = finding.target, fix = finding.fix,
                layerChange = new AreaLitAuditLayerChange
                {
                    originalLayers = change.originalLayers, projector = change.projector,
                    originalIgnoreLayers = change.originalIgnoreLayers, detectedRole = change.detectedRole, desiredRole = role
                }
            };
        }

        public static string UnavailableReason(AreaLitAuditLayerChange change)
        {
            if (change.NeedsRole) return "Choose the player or mirror role first.";
            if (change.projector == null && change.desiredRole != AreaLitProjectorRole.Unspecified)
                return "The projector is no longer available. Run the audit again.";
            if (LayerMask.NameToLayer("TransparentFX") != EmitterLayer)
                return "Restore the built-in TransparentFX layer before applying layer fixes.";
            if (change.projector != null && !ProjectorLayersAvailable)
                return "Restore VRChat's Player, PlayerLocal and MirrorReflection layers using the SDK first.";
            if (change.projector != null && (change.projector.hideFlags & HideFlags.NotEditable) != 0)
                return "The projector component is not editable.";
            var checkedScenes = new HashSet<int>();
            foreach (var target in change.originalLayers.Keys)
            {
                if (target == null || EditorUtility.IsPersistent(target) || !target.scene.IsValid() || !target.scene.isLoaded)
                    return "All affected objects must belong to loaded original scenes.";
                if ((target.hideFlags & HideFlags.NotEditable) != 0)
                    return "An affected object is not editable: " + target.name;
                if (target.GetComponents<Renderer>().Any(renderer => renderer.sharedMaterials.Any(material =>
                        material != null && material.shader != null && material.shader.name == "AreaLit/LightMesh")) &&
                    target.GetComponents<Projector>().Any(projector => projector.material != null &&
                        projector.material.shader != null && projector.material.shader.name == "AreaLit/Projector"))
                    return "Separate the emitter and projector onto different GameObjects first: " + target.name;
                if (string.IsNullOrEmpty(target.scene.path) || !checkedScenes.Add(target.scene.handle)) continue;
                if (!AssetDatabase.IsOpenForEdit(target.scene.path, StatusQueryOptions.UseCachedIfPossible))
                    return "Check out the affected scene before applying this change: " + target.scene.name;
                var path = Path.GetFullPath(target.scene.path);
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                    return "The affected scene file is missing or read-only: " + target.scene.name;
            }
            return null;
        }

        public static string ChangeDescription(AreaLitAuditLayerChange change)
        {
            if (change.projector != null)
                return "Set this projector to " + ExpectedSetup(change.desiredRole) +
                       ". Ignore every other layer, including unnamed layers. Leave its material, coverage and children unchanged.";
            return "Set " + change.originalLayers.Count + " LightMesh GameObjects to TransparentFX, without changing their children or camera masks. " +
                   "This also changes layer-based rendering, collisions and raycasts for other components on those same objects.";
        }

        public static void Apply(AreaLitAuditFinding requested, bool includeDisabled)
        {
            var current = AreaLitAudit.Scan(includeDisabled).findings.FirstOrDefault(item => item.id == requested.id && item.fix == requested.fix);
            var change = requested.layerChange;
            if (current == null || !change.MatchesSnapshot(current.layerChange))
                throw new InvalidOperationException("The affected objects, layers or projector role changed. Run the audit again.");
            var reason = UnavailableReason(change);
            if (reason != null) throw new InvalidOperationException(reason);
            var objects = change.originalLayers.Keys.Cast<Object>().ToList();
            if (change.projector != null) objects.Add(change.projector);
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var name = change.projector == null ? "Fix AreaLit emitter layers" : "Fix AreaLit projector layers";
            Undo.SetCurrentGroupName(name);
            Undo.RegisterCompleteObjectUndo(objects.ToArray(), name);
            try
            {
                foreach (var target in change.originalLayers.Keys)
                {
                    target.layer = change.projector == null ? EmitterLayer : DesiredLayer(change.desiredRole);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                    EditorSceneManager.MarkSceneDirty(target.scene);
                }
                if (change.projector != null)
                {
                    change.projector.ignoreLayers = DesiredIgnoreLayers(change.desiredRole);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(change.projector);
                }
                Undo.CollapseUndoOperations(group);
                Undo.IncrementCurrentGroup();
            }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                throw;
            }
        }
    }
}
