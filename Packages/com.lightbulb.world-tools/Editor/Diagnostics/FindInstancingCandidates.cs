// Assets/Editor/FindActionableInstancing_ListAll.cs
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.Linq;

namespace Lightbulb.WorldTools
{
public static class FindActionableInstancing_ListAll
{
    private const bool IncludeInactive = true;
    private const bool OnlySingleMaterialRenderers = true;

    [MenuItem("Tools/Lightbulb/Find GPU Instancing Candidates")]
    private static void Run()
    {
        var buckets = new Dictionary<BroadKey, List<RInfo>>();
        int skippedNullMesh = 0, skippedNullMat = 0, skippedMultiMat = 0;

        foreach (var mr in Object.FindObjectsOfType<MeshRenderer>(IncludeInactive))
        {
            if (mr == null) continue;

            var mf = mr.GetComponent<MeshFilter>();
            var mesh = mf ? mf.sharedMesh : null;
            if (mesh == null) { skippedNullMesh++; continue; }

            var mats = mr.sharedMaterials;
            if (mats == null || mats.Length == 0 || mats[0] == null) { skippedNullMat++; continue; }
            if (OnlySingleMaterialRenderers && mats.Length != 1) { skippedMultiMat++; continue; }
            var mat = mats[0];

            var key = new BroadKey(mesh.GetInstanceID(), mesh.name, mat.GetInstanceID(), mat.name);
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = new List<RInfo>();

            list.Add(new RInfo(mr, mesh, mat));
        }

        int actionableCount = 0;

        foreach (var kv in buckets.OrderByDescending(kv => kv.Value.Count))
        {
            var broad = kv.Key;
            var list = kv.Value;

            // Partition by “instancing-compatibility” (things that must match to draw in one instanced call)
            var compatGroups = list.GroupBy(r => r.Compat).OrderByDescending(g => g.Count()).ToList();
            if (compatGroups.Count == 0) continue;

            // Focus on the largest compat subgroup (most likely win)
            var topCompat = compatGroups.First();
            var eligible = topCompat.Where(r => r.EligibleForInstancing).ToList();
            bool matInstancing = list[0].Mat && list[0].Mat.enableInstancing;

            // ACTIONABLE if the material has instancing OFF but we have 2+ eligible
            if (!matInstancing && eligible.Count >= 2)
            {
                actionableCount++;
                LogActionable_AllNames(broad, topCompat.Key, eligible, list[0].Mat, totalOfThisMeshMat: list.Count);
            }
        }

        if (actionableCount == 0)
            Debug.Log("No actionable instancing opportunities found (materials already have instancing ON or too few eligible).");

        Debug.Log($"Done. Skipped: null mesh={skippedNullMesh}, null mat={skippedNullMat}, multi-mat={skippedMultiMat}.");
    }

    // ---------- logging (lists ALL names) ----------

    private static void LogActionable_AllNames(BroadKey broad, CompatKey compat, List<RInfo> eligibles, Material mat, int totalOfThisMeshMat)
    {
        Debug.Log(
            $"▶ {eligibles.Count} of {totalOfThisMeshMat} can instance  |  " +
            $"mesh='{broad.MeshName}'  mat='{broad.MatName}'  " +
            $"(LM#{compat.LightmapIndex}, probes={compat.ProbeKey}, cast={compat.ShadowMode}, recvShad={(compat.ReceiveShadows ? "Y" : "N")}, " +
            $"lightProbe={compat.LightProbe}, motionVec={compat.MotionVectors}, rLayer=0x{compat.RenderingLayerMask:X})",
            mat
        );

        // Print ALL eligible object names (full hierarchy paths), one per line.
        foreach (var r in eligibles.OrderBy(e => e.Renderer.gameObject.name))
        {
            string flags = "";
            if (r.HasPropertyBlock) flags += " [PropertyBlock]";
            Debug.Log($"    • {FullPath(r.Renderer.transform)}{flags}", r.Renderer.gameObject);
        }

        Debug.Log("    ➤ Action: enable *GPU Instancing* on this material (shader must support it).", mat);
    }

    // ---------- types & helpers ----------

    private readonly struct BroadKey
    {
        public readonly int MeshId; public readonly string MeshName;
        public readonly int MatId; public readonly string MatName;
        public BroadKey(int meshId, string meshName, int matId, string matName)
        { MeshId = meshId; MeshName = meshName; MatId = matId; MatName = matName; }
        public override int GetHashCode() => MeshId * 16777619 ^ MatId * 486187739;
        public override bool Equals(object o) => o is BroadKey b && b.MeshId == MeshId && b.MatId == MatId;
    }

    private readonly struct CompatKey
    {
        public readonly int LightmapIndex;
        public readonly string ProbeKey; // Off | Anchor:path | Auto:id+id | SkyboxOnly
        public readonly ShadowCastingMode ShadowMode;
        public readonly bool ReceiveShadows;
        public readonly LightProbeUsage LightProbe;
        public readonly MotionVectorGenerationMode MotionVectors;
        public readonly uint RenderingLayerMask;

        public CompatKey(Renderer r)
        {
            LightmapIndex = r.lightmapIndex;
            ProbeKey = BuildProbeKey(r);
            ShadowMode = r.shadowCastingMode;
            ReceiveShadows = r.receiveShadows;
            LightProbe = r.lightProbeUsage;
            MotionVectors = r.motionVectorGenerationMode;
            RenderingLayerMask = r.renderingLayerMask;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int h = LightmapIndex;
                h = h * 31 + (ProbeKey?.GetHashCode() ?? 0);
                h = h * 31 + (int)ShadowMode;
                h = h * 31 + (ReceiveShadows ? 1 : 0);
                h = h * 31 + (int)LightProbe;
                h = h * 31 + (int)MotionVectors;
                h = h * 31 + (int)RenderingLayerMask;
                return h;
            }
        }

        public override bool Equals(object o)
        {
            if (!(o is CompatKey k)) return false;
            return LightmapIndex == k.LightmapIndex &&
                   ProbeKey == k.ProbeKey &&
                   ShadowMode == k.ShadowMode &&
                   ReceiveShadows == k.ReceiveShadows &&
                   LightProbe == k.LightProbe &&
                   MotionVectors == k.MotionVectors &&
                   RenderingLayerMask == k.RenderingLayerMask;
        }
    }

    private sealed class RInfo
    {
        public readonly MeshRenderer Renderer;
        public readonly Mesh Mesh;
        public readonly Material Mat;
        public readonly bool StaticBatched;
        public readonly bool ActiveEnabled;
        public readonly bool HasPropertyBlock;
        public readonly CompatKey Compat;

        // Eligible = active+enabled and NOT static-batched (instancing material toggle checked separately)
        public bool EligibleForInstancing => ActiveEnabled && !StaticBatched;

        public RInfo(MeshRenderer mr, Mesh mesh, Material mat)
        {
            Renderer = mr; Mesh = mesh; Mat = mat;
            StaticBatched = GameObjectUtility.AreStaticEditorFlagsSet(mr.gameObject, StaticEditorFlags.BatchingStatic);
            ActiveEnabled = mr.enabled && mr.gameObject.activeInHierarchy;

            var mpb = new MaterialPropertyBlock();
            mr.GetPropertyBlock(mpb);
#if UNITY_2021_2_OR_NEWER
            HasPropertyBlock = !mpb.isEmpty;
#else
            HasPropertyBlock = true; // pre-2021.2 detection is unreliable
#endif
            Compat = new CompatKey(mr);
        }
    }

    private static string BuildProbeKey(Renderer r)
    {
        if (r.reflectionProbeUsage == ReflectionProbeUsage.Off) return "Off";
        if (r.probeAnchor) return "Anchor:" + FullPath(r.probeAnchor);

        var infos = new List<ReflectionProbeBlendInfo>();
        r.GetClosestReflectionProbes(infos);
        if (infos.Count == 0) return "SkyboxOnly";

        var top = infos.OrderByDescending(i => i.weight)
                       .Take(2)
                       .Select(i => i.probe ? i.probe.GetInstanceID().ToString() : "null");
        return "Auto:" + string.Join("+", top);
    }

    private static string FullPath(Transform t)
    {
        var names = new List<string>();
        while (t != null) { names.Add(t.name); t = t.parent; }
        names.Reverse();
        return string.Join("/", names);
    }
}
}
