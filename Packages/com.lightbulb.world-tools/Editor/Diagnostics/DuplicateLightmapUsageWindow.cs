using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.WorldTools
{
    internal sealed class DuplicateLightmapUsageWindow : EditorWindow
    {
        [SerializeField] bool includeInactive = true;
        [SerializeField] int sort;
        Vector2 scroll;
        DuplicateLightmapUsage.ScanResult scan;
        string error, status;
        static readonly string[] SortLabels = { "Estimated footprint", "Lightmapped count", "Instance count" };

        [MenuItem("Tools/Lightbulb/Duplicate Lightmap Usage")]
        internal static void Open() => GetWindow<DuplicateLightmapUsageWindow>("Duplicate Lightmap Usage").Show();
        void OnEnable() { minSize = new Vector2(780, 420); Undo.undoRedoPerformed += Invalidate; EditorApplication.hierarchyChanged += Invalidate; }
        void OnDisable() { Undo.undoRedoPerformed -= Invalidate; EditorApplication.hierarchyChanged -= Invalidate; }
        void Invalidate() { scan = null; Repaint(); }
        void Run(Action action) { try { error = null; action(); } catch (Exception e) { error = e.Message; } Repaint(); }
        void Scan() { scan = DuplicateLightmapUsage.Scan(SceneMaterials.Active(), includeInactive); }
        void Apply(DuplicateLightmapUsage.Group group, float? scale, bool probes)
        {
            Run(() => { DuplicateLightmapUsage.Apply(scan, group, scale, probes); Scan(); status = "Settings changed. Rebake to update atlas allocations; Edit > Undo restores the edit."; });
            GUIUtility.ExitGUI();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Duplicate Lightmap Usage", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Groups the active scene's Mesh Renderers by shared mesh and every material slot. Footprint estimates describe the current bake; settings changes require a rebake. Smaller or fewer atlases determine actual memory savings.", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                includeInactive = EditorGUILayout.ToggleLeft("Include inactive objects", includeInactive, GUILayout.Width(190));
                if (EditorGUI.EndChangeCheck()) scan = null;
                sort = EditorGUILayout.Popup(sort, SortLabels, GUILayout.Width(180));
                if (GUILayout.Button("Scan active scene")) Run(() => { Scan(); status = null; });
            }
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (status != null) EditorGUILayout.HelpBox(status, MessageType.Info);
            if (scan == null) return;
            if (scan.Scene != UnityEngine.SceneManagement.SceneManager.GetActiveScene()) { scan = null; return; }
            EditorGUILayout.LabelField(scan.Groups.Length + " duplicate categories / " + scan.Groups.Sum(g => g.Renderers.Length) + " renderers");
            if (scan.MissingMeshes > 0) EditorGUILayout.HelpBox(scan.MissingMeshes + " renderers have no mesh and were skipped.", MessageType.Warning);
            string blocked = null;
            try { SceneMaterials.RequireLightingScene(scan.Scene); } catch (Exception e) { blocked = e.Message; }
            if (blocked != null) EditorGUILayout.HelpBox(blocked, MessageType.Warning);
            var groups = sort == 1 ? scan.Groups.OrderByDescending(g => g.BakedCount).ThenByDescending(g => g.Texels)
                : sort == 2 ? scan.Groups.OrderByDescending(g => g.Renderers.Length).ThenByDescending(g => g.Texels)
                : scan.Groups.OrderByDescending(g => g.Texels).ThenByDescending(g => g.BakedCount);
            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                foreach (var group in groups)
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            group.Expanded = EditorGUILayout.Foldout(group.Expanded, group.Mesh.name, true);
                            if (GUILayout.Button("Select objects", GUILayout.Width(105)))
                                Selection.objects = group.Renderers.Where(r => r != null).Select(r => r.gameObject).ToArray();
                        }
                        EditorGUILayout.LabelField("Materials: " + (group.Materials.Length == 0 ? "None" : string.Join(" / ", group.Materials.Select(m => m == null ? "Missing" : m.name))));
                        string footprint = group.BakedCount == 0 ? "No current bake allocation" : "≈ " + group.Texels.ToString("N0") + " atlas pixels";
                        if (group.UnknownFootprints > 0) footprint += " + " + group.UnknownFootprints + " unknown";
                        EditorGUILayout.LabelField(group.Renderers.Length + " instances  |  " + group.BakedCount + " currently lightmapped  |  " + group.ConfiguredCount + " configured  |  " + footprint);
                        if (group.InactiveCount > 0 || group.DisabledCount > 0)
                            EditorGUILayout.LabelField(group.InactiveCount + " inactive / " + group.DisabledCount + " disabled renderers", EditorStyles.miniLabel);
                        if (group.OverlappingAllocations)
                            EditorGUILayout.LabelField("Overlapping atlas rectangles: overlaps counted once within this category; category totals may overlap.", EditorStyles.wordWrappedMiniLabel);
                        var receivers = group.Renderers.Where(r => r != null && DuplicateLightmapUsage.ReceivesLightmaps(r)).ToArray();
                        var scales = receivers.Select(RendererLightingSettings.Scale).Distinct().ToArray();
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label("Scale: " + (scales.Length == 0 ? "N/A" : scales.Length == 1 ? scales[0].ToString("0.###") : "Mixed"), GUILayout.Width(100));
                            using (new EditorGUI.DisabledScope(blocked != null || receivers.Length == 0))
                            {
                                group.PendingScale = EditorGUILayout.FloatField(group.PendingScale, GUILayout.Width(70));
                                using (new EditorGUI.DisabledScope(!float.IsFinite(group.PendingScale) || group.PendingScale < 0))
                                    if (GUILayout.Button("Apply scale", GUILayout.Width(95))) Apply(group, group.PendingScale, false);
                                if (GUILayout.Button("Set to 0", GUILayout.Width(75))) Apply(group, 0, false);
                            }
                            GUILayout.FlexibleSpace();
                            using (new EditorGUI.DisabledScope(blocked != null))
                                if (GUILayout.Button("Use light probes", GUILayout.Width(125))) Apply(group, null, true);
                        }
                        if (group.Expanded)
                        {
                            EditorGUILayout.HelpBox("Scale edits affect matching GI contributors receiving Lightmaps. Use light probes sets Receive GI to Light Probes and probe usage to Blend Probes for all matching renderers. It preserves scale, Contribute GI, materials, and Light Volume settings. Probe lighting needs suitable baked probes and shader support.", MessageType.Info);
                            EditorGUILayout.ObjectField("Mesh", group.Mesh, typeof(Mesh), false);
                            foreach (var material in group.Materials) EditorGUILayout.ObjectField(material, typeof(Material), false);
                            foreach (var renderer in group.Renderers) EditorGUILayout.ObjectField(renderer, typeof(MeshRenderer), true);
                        }
                    }
            }
        }
    }
}
