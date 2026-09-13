using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Lightbulb.WorldTools
{
    internal static class LightingVolumeBounds
    {
        internal static Bounds Enclose(IEnumerable<Bounds> source)
        {
            var values = source.ToArray();
            if (values.Length == 0) throw new InvalidOperationException("No geometry bounds were found. Choose geometry layers containing the world.");
            Bounds result = values[0];
            foreach (Bounds b in values.Skip(1)) result.Encapsulate(b);
            return result;
        }
        static double Cost(Bounds b) => Math.Max(.001, b.size.x) * Math.Max(.001, b.size.y) * Math.Max(.001, b.size.z);
        internal static List<Bounds> Fit(IEnumerable<Bounds> source, int maximum, float padding)
        {
            if (maximum < 1 || maximum > 10 || padding < 0 || float.IsNaN(padding) || float.IsInfinity(padding)) throw new ArgumentException("Choose 1–10 volumes and finite nonnegative padding.");
            var values = source.OrderBy(b => b.center.x).ThenBy(b => b.center.y).ThenBy(b => b.center.z).ThenBy(b => b.size.x).ThenBy(b => b.size.y).ThenBy(b => b.size.z).ToList();
            Enclose(values);
            var groups = new List<List<Bounds>> { values };
            while (groups.Count < maximum)
            {
                double saving = 0; int chosen = -1; List<Bounds> left = null, right = null;
                for (int g = 0; g < groups.Count; g++)
                    for (int axis = 0; axis < 3; axis++)
                    {
                        var sorted = groups[g].OrderBy(b => b.center[axis]).ToList();
                        if (sorted.Count < 2) continue;
                        // Fixed candidate positions: independent of selection order or random seeds.
                        foreach (int cut in new[] { sorted.Count / 4, sorted.Count / 2, sorted.Count * 3 / 4 }.Distinct())
                        {
                            if (cut <= 0 || cut >= sorted.Count) continue;
                            var a = sorted.Take(cut).ToList(); var b = sorted.Skip(cut).ToList();
                            Bounds original = Enclose(sorted), ba = Enclose(a), bb = Enclose(b);
                            original.Expand(padding * 2); ba.Expand(padding * 2); bb.Expand(padding * 2);
                            double benefit = Cost(original) - Cost(ba) - Cost(bb);
                            if (benefit > Cost(original) * .1 && benefit > saving) { chosen = g; saving = benefit; left = a; right = b; }
                        }
                    }
                if (chosen < 0) break;
                groups[chosen] = left; groups.Insert(chosen + 1, right);
            }
            return groups.Select(g => { Bounds b = Enclose(g); b.Expand(padding * 2); b.size = Vector3.Max(b.size, Vector3.one * .1f); return b; }).ToList();
        }
    }
}
