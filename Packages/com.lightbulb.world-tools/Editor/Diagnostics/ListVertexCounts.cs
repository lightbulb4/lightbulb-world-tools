// Assets/Editor/ListVertexCounts.cs
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lightbulb.WorldTools
{
public static class ListVertexCounts
{
    // Tweak these to taste
    private const bool IncludeInactive = true;
    private const int MinVertexFilter = 0;    // e.g., set to 100 to hide tiny meshes

    [MenuItem("Tools/Lightbulb/Rank by Vertex Count")]
    private static void RankRenderers()
    {
        var rows = new List<(Object ctx, string path, int verts)>();

        // MeshRenderers (static meshes)
        foreach (var mr in Object.FindObjectsOfType<MeshRenderer>(IncludeInactive))
        {
            var mf = mr.GetComponent<MeshFilter>();
            var mesh = mf ? mf.sharedMesh : null;
            if (mesh == null) continue;

            int v = mesh.vertexCount;
            if (v < MinVertexFilter) continue;

            rows.Add((mr.gameObject, GetPath(mr.transform), v));
        }

        // SkinnedMeshRenderers (rigged/animated)
        foreach (var smr in Object.FindObjectsOfType<SkinnedMeshRenderer>(IncludeInactive))
        {
            var mesh = smr.sharedMesh;
            if (mesh == null) continue;

            int v = mesh.vertexCount;
            if (v < MinVertexFilter) continue;

            rows.Add((smr.gameObject, GetPath(smr.transform), v));
        }

        if (rows.Count == 0)
        {
            Debug.Log("No renderers with meshes found.");
            return;
        }

        int total = rows.Sum(r => r.verts);

        foreach (var r in rows.OrderByDescending(r => r.verts))
            Debug.Log($"🧱 {r.verts,8} verts   {r.path}", r.ctx);

        Debug.Log($"Renderer ranking complete — {rows.Count} renderers, total {total:N0} verts.");
    }

    private static string GetPath(Transform t)
    {
        var names = new List<string>();
        while (t != null)
        {
            names.Add(t.name);
            t = t.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }
}
}
