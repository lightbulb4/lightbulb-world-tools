using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools
{
    internal static class DuplicateLightmapUsage
    {
        internal sealed class Group
        {
            internal Scene Scene;
            internal Mesh Mesh;
            internal Material[] Materials;
            internal MeshRenderer[] Renderers;
            internal string Fingerprint;
            internal int BakedCount, ConfiguredCount, InactiveCount, DisabledCount, UnknownFootprints;
            internal double Texels;
            internal bool OverlappingAllocations;
            internal float PendingScale = 1;
            internal bool Expanded;
        }

        internal sealed class ScanResult
        {
            internal Scene Scene;
            internal bool IncludeInactive;
            internal Group[] Groups;
            internal int MissingMeshes;
        }

        sealed class Key : IEquatable<Key>
        {
            readonly Mesh mesh;
            readonly Material[] materials;
            internal Key(Mesh mesh, Material[] materials) { this.mesh = mesh; this.materials = materials; }
            public bool Equals(Key other) => other != null && mesh == other.mesh && materials.SequenceEqual(other.materials);
            public override bool Equals(object other) => Equals(other as Key);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = mesh.GetInstanceID();
                    foreach (var material in materials) hash = hash * 31 + (material == null ? 0 : material.GetInstanceID());
                    return hash;
                }
            }
        }

        sealed class Allocation
        {
            internal Group Group;
            internal Rect Rect;
            internal int Atlas, Width, Height;
        }

        internal static MeshRenderer[] Renderers(Scene scene, bool includeInactive) => scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<MeshRenderer>(includeInactive))
            .Where(r => includeInactive || r.gameObject.activeInHierarchy).ToArray();

        static Mesh MeshOf(MeshRenderer renderer)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            return filter == null ? null : filter.sharedMesh;
        }

        internal static bool ReceivesLightmaps(MeshRenderer renderer) =>
            GameObjectUtility.AreStaticEditorFlagsSet(renderer.gameObject, StaticEditorFlags.ContributeGI)
            && renderer.receiveGI == ReceiveGI.Lightmaps;

        internal static ScanResult Scan(Scene scene, bool includeInactive)
        {
            SceneMaterials.RequireActive(scene);
            var result = new ScanResult { Scene = scene, IncludeInactive = includeInactive };
            var buckets = new Dictionary<Key, List<MeshRenderer>>();
            foreach (var renderer in Renderers(scene, includeInactive))
            {
                var mesh = MeshOf(renderer);
                if (mesh == null) { result.MissingMeshes++; continue; }
                var key = new Key(mesh, renderer.sharedMaterials);
                if (!buckets.TryGetValue(key, out var members)) buckets.Add(key, members = new List<MeshRenderer>());
                members.Add(renderer);
            }
            var groups = new List<Group>();
            var allocations = new List<Allocation>();
            var maps = LightmapSettings.lightmaps;
            foreach (var members in buckets.Values)
            {
                var first = members[0];
                var group = new Group
                {
                    Scene = scene, Mesh = MeshOf(first), Materials = first.sharedMaterials,
                    Renderers = members.OrderBy(r => r.GetInstanceID()).ToArray(),
                    InactiveCount = members.Count(r => !r.gameObject.activeInHierarchy),
                    DisabledCount = members.Count(r => !r.enabled),
                    ConfiguredCount = members.Count(r => ReceivesLightmaps(r) && RendererLightingSettings.Scale(r) > 0)
                };
                foreach (var renderer in members)
                {
                    int index = renderer.lightmapIndex;
                    if (index < 0 || index >= 0xfffe || index >= maps.Length) continue;
                    var map = maps[index];
                    var texture = map?.lightmapColor != null ? map.lightmapColor : map?.lightmapDir;
                    if (texture == null) continue;
                    group.BakedCount++;
                    var so = renderer.lightmapScaleOffset;
                    if (!float.IsFinite(so.x) || !float.IsFinite(so.y) || !float.IsFinite(so.z) || !float.IsFinite(so.w))
                    { group.UnknownFootprints++; continue; }
                    var rect = Rect.MinMaxRect(Mathf.Clamp01(Mathf.Min(so.z, so.z + so.x)), Mathf.Clamp01(Mathf.Min(so.w, so.w + so.y)),
                        Mathf.Clamp01(Mathf.Max(so.z, so.z + so.x)), Mathf.Clamp01(Mathf.Max(so.w, so.w + so.y)));
                    if (rect.width <= 0 || rect.height <= 0) { group.UnknownFootprints++; continue; }
                    allocations.Add(new Allocation { Group = group, Atlas = index, Rect = rect, Width = texture.width, Height = texture.height });
                }
                foreach (var atlas in allocations.Where(a => a.Group == group).GroupBy(a => a.Atlas))
                    group.Texels += UnionArea(atlas.Select(a => a.Rect).ToArray()) * atlas.First().Width * atlas.First().Height;
                group.Fingerprint = Fingerprint(group.Renderers);
                var scales = members.Where(ReceivesLightmaps).Select(RendererLightingSettings.Scale).Distinct().ToArray();
                if (scales.Length == 1) group.PendingScale = scales[0];
                // Keep zero-scale and probe categories visible so users can inspect the current bake after editing.
                if (members.Count >= 2 && (group.BakedCount > 0 || members.Any(r =>
                    GameObjectUtility.AreStaticEditorFlagsSet(r.gameObject, StaticEditorFlags.ContributeGI)))) groups.Add(group);
            }
            // Include singletons and other categories when detecting overlapping allocations.
            foreach (var atlas in allocations.GroupBy(a => a.Atlas))
            {
                var active = new List<Allocation>();
                foreach (var allocation in atlas.OrderBy(a => a.Rect.xMin))
                {
                    active.RemoveAll(a => a.Rect.xMax <= allocation.Rect.xMin);
                    foreach (var other in active)
                        if (other.Rect.yMin < allocation.Rect.yMax && other.Rect.yMax > allocation.Rect.yMin)
                        { other.Group.OverlappingAllocations = true; allocation.Group.OverlappingAllocations = true; }
                    active.Add(allocation);
                }
            }
            result.Groups = groups.OrderByDescending(g => g.Texels).ThenByDescending(g => g.BakedCount)
                .ThenBy(g => g.Mesh.name, StringComparer.Ordinal).ToArray();
            return result;
        }

        internal static double UnionArea(Rect[] rectangles)
        {
            var rects = rectangles.Distinct().ToArray();
            var edges = rects.SelectMany(r => new[] { r.xMin, r.xMax }).Distinct().OrderBy(x => x).ToArray();
            double area = 0;
            for (int i = 1; i < edges.Length; i++)
            {
                var strip = rects.Where(r => r.xMin < edges[i] && r.xMax > edges[i - 1]).OrderBy(r => r.yMin).ToArray();
                if (strip.Length == 0) continue;
                double height = 0, bottom = strip[0].yMin, top = strip[0].yMax;
                foreach (var rect in strip.Skip(1))
                {
                    if (rect.yMin > top) { height += top - bottom; bottom = rect.yMin; }
                    top = Math.Max(top, rect.yMax);
                }
                area += (edges[i] - edges[i - 1]) * (height + top - bottom);
            }
            return area;
        }

        static string Fingerprint(IEnumerable<MeshRenderer> renderers) => string.Join(";", renderers.OrderBy(r => r.GetInstanceID()).Select(r =>
            r.GetInstanceID() + ":" + MeshOf(r).GetInstanceID() + ":"
            + string.Join(",", r.sharedMaterials.Select(m => m == null ? 0 : m.GetInstanceID())) + ":"
            + (int)GameObjectUtility.GetStaticEditorFlags(r.gameObject) + ":" + (int)r.receiveGI + ":" + (int)r.lightProbeUsage + ":"
            + RendererLightingSettings.Scale(r).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":"
            + r.enabled + ":" + r.gameObject.activeInHierarchy));

        internal static void Apply(ScanResult scan, Group group, float? scale, bool useProbes)
        {
            SceneMaterials.RequireLightingScene(scan.Scene);
            if (scale.HasValue == useProbes) throw new ArgumentException("Choose either a scale edit or a probe edit.");
            if (scale.HasValue && (!float.IsFinite(scale.Value) || scale.Value < 0)) throw new ArgumentOutOfRangeException(nameof(scale));
            if (!scan.Groups.Contains(group)) throw new InvalidOperationException("Scan again before applying.");
            var key = new Key(group.Mesh, group.Materials);
            var current = Renderers(scan.Scene, scan.IncludeInactive).Where(r => MeshOf(r) != null
                && key.Equals(new Key(MeshOf(r), r.sharedMaterials))).ToArray();
            if (current.Length != group.Renderers.Length || Fingerprint(current) != group.Fingerprint)
                throw new InvalidOperationException("The category changed. Scan again before applying.");
            var targets = (useProbes ? current : current.Where(ReceivesLightmaps)).ToArray();
            if (targets.Length == 0) throw new InvalidOperationException("This category has no matching lightmap receivers.");
            if (targets.Any(r => (r.hideFlags & HideFlags.NotEditable) != 0 || (r.gameObject.hideFlags & HideFlags.NotEditable) != 0))
                throw new InvalidOperationException("A matching renderer is not editable.");
            string operation = useProbes ? "Use light probes for duplicate category" : "Set duplicate category lightmap scale";
            Undo.IncrementCurrentGroup();
            int undo = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(operation);
            Undo.RecordObjects(targets.Cast<Object>().ToArray(), operation);
            try
            {
                foreach (var renderer in targets)
                {
                    if (scale.HasValue) RendererLightingSettings.SetScale(renderer, scale.Value);
                    else
                    {
                        renderer.receiveGI = ReceiveGI.LightProbes;
                        renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    }
                }
                EditorSceneManager.MarkSceneDirty(scan.Scene);
                Undo.CollapseUndoOperations(undo);
            }
            catch { Undo.RevertAllDownToGroup(undo); throw; }
        }
    }
}
