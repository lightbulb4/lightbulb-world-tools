// Assets/Editor/FindLightmapTexelHogs.cs
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Lightbulb.WorldTools
{
public static class FindLightmapTexelHogs
{
    [MenuItem("Tools/Lightbulb/Rank by Lightmap Texel Usage")]
    private static void Find()
    {
        var lms = LightmapSettings.lightmaps;

        if (lms == null || lms.Length == 0)
        {
            Debug.LogWarning("no baked light-maps found. bake first, then run again.");
            return;
        }

        var hogs = new List<(Renderer r, float texels, int lmIndex)>();
        int skippedIdxOob = 0, skippedNullEntry = 0, skippedNoColor = 0;

        // include inactive too; otherwise you’ll miss the real sinners
#if UNITY_2020_1_OR_NEWER
        var renderers = Object.FindObjectsOfType<Renderer>(true);
#else
        var renderers = Object.FindObjectsOfType<Renderer>();
#endif

        foreach (var r in renderers)
        {
            if (r == null) continue;

            // must actually contribute to baked GI or it has no meaningful LM footprint
            if (!GameObjectUtility.AreStaticEditorFlagsSet(r.gameObject, StaticEditorFlags.ContributeGI))
                continue;

            int idx = r.lightmapIndex;
            if (idx < 0) continue;

            // stale/invalid index? super common after deleting/rebaking/merging scenes
            if (idx >= lms.Length)
            {
                skippedIdxOob++;
                continue;
            }

            var entry = lms[idx];
            if (entry.lightmapColor == null && entry.lightmapDir == null)
            {
                // entry is present but has no textures (can happen w/ stripped bakes)
                skippedNullEntry++;
                continue;
            }

            var lm = entry.lightmapColor;
            if (lm == null)
            {
                // some projects bake directional only; fall back to dir map dims
                var dir = entry.lightmapDir;
                if (dir == null) { skippedNoColor++; continue; }
                // we only need dimensions, not pixels
                Accumulate(r, dir.width, dir.height, idx, hogs);
            }
            else
            {
                Accumulate(r, lm.width, lm.height, idx, hogs);
            }
        }

        foreach (var h in hogs.OrderByDescending(h => h.texels))
        {
            Debug.Log(
                $"📦  {h.r.gameObject.name,-35}  " +
                $"≈ {h.texels:N0} texels  " +
                $"(lm #{h.lmIndex})",
                h.r.gameObject);
        }

        Debug.Log(
            $"scan complete — {hogs.Count} light-mapped objects. " +
            $"skipped: idx_oob={skippedIdxOob}, null_entry={skippedNullEntry}, no_color={skippedNoColor}");
    }

    private static void Accumulate(Renderer r, int lmW, int lmH, int idx, List<(Renderer, float, int)> hogs)
    {
        // lightmapScaleOffset: (scaleX, scaleY, offX, offY)
        // use abs in case someone “got creative” with negative tiling
        Vector4 so = r.lightmapScaleOffset;
        float sx = Mathf.Abs(so.x);
        float sy = Mathf.Abs(so.y);

        // clamp absurd values; occasionally corrupt data = >1 (or NaN)
        if (!float.IsFinite(sx) || !float.IsFinite(sy)) return;
        sx = Mathf.Clamp01(sx);
        sy = Mathf.Clamp01(sy);

        float pixels = sx * lmW * sy * lmH;
        if (pixels <= 0f) return;

        hogs.Add((r, pixels, idx));
    }
}
}
