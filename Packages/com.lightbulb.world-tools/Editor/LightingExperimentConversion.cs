using System;
using UnityEditor;
using UnityEngine;
using static Lightbulb.WorldTools.LightingExperimentAdapter;

namespace Lightbulb.WorldTools
{
    internal static class LightingExperimentConversion
    {
        internal sealed class Candidate
        {
            internal Component source;
            internal bool included = true;
            internal bool toBakery, forcePoint;
            internal string description, problem;
        }
        internal static Candidate Inspect(Component source, bool forcePoint)
        {
            var c = new Candidate { source = source, forcePoint = forcePoint };
            string type = source.GetType().Name;
            if (type == "PointLightVolumeInstance")
            {
                c.toBakery = true;
                int lightType = Get<int>(source, "LightType"), projection = Get<int>(source, "Projection");
                if (projection == 1 && !forcePoint) c.problem = "LUT falloff has no supported Bakery mapping. Force points explicitly discards it.";
                else if (lightType == 2 && !forcePoint) c.description = "Bakery rectangular mesh emitter (approximate brightness)";
                else c.description = forcePoint || lightType == 0 ? "Bakery point (approximate brightness/range)" : "Bakery spot (approximate brightness/range)";
                if (!forcePoint && projection == 2 && lightType == 1 && (!(Get(source, "Cookie") is Texture2D) || !Mathf.Approximately(Get<float>(source, "SpotCookieAspect"), 1))) c.problem = "Bakery requires a Texture2D spot cookie with square projection.";
                if (!forcePoint && projection == 2 && lightType == 0 && !(Get(source, "Cubemap") is Cubemap)) c.problem = "Bakery requires a Cubemap asset for this projection.";
                var areaCookie = Get(source, "Cookie") as UnityEngine.Object;
                if (!forcePoint && lightType == 2 && areaCookie != null && !(areaCookie is Texture2D)) c.problem = "Bakery area emission requires a Texture2D cookie.";
            }
            else if (type == "BakeryPointLight")
            {
                int projection = Convert.ToInt32(Get(source, "projMode"));
                if (projection == 3 && !forcePoint) c.problem = "IES projection needs a LUT conversion; use Force point lights to discard its projection.";
                else c.description = forcePoint || projection == 0 || projection == 2 ? "Point LV (approximate brightness/range)" : "Spot LV (approximate brightness/range)";
            }
            else if (type == "BakeryLightMesh" && !forcePoint && Rectangle(source, out _)) c.description = "Area LV from a rectangular XY quad (approximate brightness)";
            else if (forcePoint)
                c.description = type == "BakeryLightMesh" ? "Point LV at emitter bounds center; emission shape/texture discarded" : "Point LV at world center (sky) or above world (sun); approximate lighting";
            else c.problem = "No automatic equivalent for " + type + ". Force point lights provides an explicit approximation.";
            c.included = c.problem == null;
            return c;
        }
        internal static bool Rectangle(Component source, out Vector2 size)
        {
            size = Vector2.zero;
            Mesh mesh = source.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null || !mesh.isReadable || mesh.vertexCount != 4 || mesh.triangles.Length != 6) return false;
            Bounds b = mesh.bounds;
            if (b.size.z > .0001f || b.size.x <= 0 || b.size.y <= 0) return false;
            foreach (Vector3 v in mesh.vertices)
                if (Mathf.Min(Mathf.Abs(v.x - b.min.x), Mathf.Abs(v.x - b.max.x)) > .0001f || Mathf.Min(Mathf.Abs(v.y - b.min.y), Mathf.Abs(v.y - b.max.y)) > .0001f) return false;
            Vector3 scale = source.transform.lossyScale;
            size = new Vector2(b.size.x * Mathf.Abs(scale.x), b.size.y * Mathf.Abs(scale.y));
            return size.x > .001f && size.y > .001f;
        }
        internal static Component Create(Candidate c, LightingExperimentAdapter adapter, Bounds world, float brightness, string outputFolder)
        {
            if (c.source == null || c.problem != null || !float.IsFinite(brightness) || brightness <= 0) throw new InvalidOperationException("Invalid conversion. Scan again.");
            Component result;
            if (c.toBakery)
            {
                if (!c.forcePoint && Get<int>(c.source, "LightType") == 2)
                {
                    var quad = GameObject.CreatePrimitive(PrimitiveType.Quad); quad.name = c.source.name + " [Bakery area]";
                    Undo.RegisterCreatedObjectUndo(quad, "Create Bakery area counterpart");
                    Undo.DestroyObjectImmediate(quad.GetComponent<Collider>());
                    result = Undo.AddComponent(quad, adapter.BakeryMesh);
                    result.transform.SetPositionAndRotation(c.source.transform.position, c.source.transform.rotation);
                    result.transform.localScale = new Vector3(Mathf.Abs(c.source.transform.lossyScale.x), Mathf.Abs(c.source.transform.lossyScale.y), 1);
                    Set(result, "color", Get<Color>(c.source, "Color")); Set(result, "intensity", Get<float>(c.source, "Intensity") * brightness);
                    Set(result, "cutoff", Mathf.Sqrt(Mathf.Max(0, Get<float>(c.source, "SquaredRange"))));
                    var texture = Get(c.source, "Cookie") as UnityEngine.Object;
                    if (texture != null && !(texture is Texture2D)) throw new InvalidOperationException("Bakery area emission requires a Texture2D cookie.");
                    Set(result, "texture", texture as Texture2D);
                    var material = new Material(Shader.Find("Unlit/Color")); material.color = Get<Color>(c.source, "Color");
                    AssetDatabase.CreateAsset(material, AssetDatabase.GenerateUniqueAssetPath(outputFolder + "/AreaEmitter.mat"));
                    quad.GetComponent<Renderer>().sharedMaterial = material;
                    ((Behaviour)result).enabled = ((Behaviour)c.source).enabled && c.source.gameObject.activeInHierarchy;
                    return result;
                }
                var go = new GameObject(c.source.name + " [Bakery]"); Undo.RegisterCreatedObjectUndo(go, "Create Bakery counterpart");
                result = Undo.AddComponent(go, adapter.BakeryPoint);
                CopyToBakery(c.source, result, c.forcePoint, brightness);
            }
            else
            {
                result = adapter.Create(true, c.source.name + " [Light Volumes]");
                CopyToVolume(c.source, result, c.forcePoint, world, brightness);
                adapter.Sync(result);
            }
            ((Behaviour)result).enabled = ((Behaviour)c.source).enabled && c.source.gameObject.activeInHierarchy;
            return result;
        }
        internal static void CopyToVolume(Component source, Component target, bool forcePoint, Bounds world, float brightness)
        {
            target.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            target.transform.localScale = Vector3.one;
            string type = source.GetType().Name;
            Set(target, "Color", Get<Color>(source, "color"));
            Set(target, "LightType", 0); Set(target, "Projection", 0);
            if (type == "BakeryLightMesh" && !forcePoint && Rectangle(source, out Vector2 dimensions))
            {
                target.transform.position = source.GetComponent<Renderer>().bounds.center;
                target.transform.localScale = new Vector3(dimensions.x, dimensions.y, 1);
                Set(target, "LightType", 2); Set(target, "Cookie", Get(source, "texture"));
                Set(target, "Intensity", Get<float>(source, "intensity") * brightness);
                Set(target, "Shadows", true); Set(target, "RebakeShadows", true); Set(target, "BakeIntoProbes", false);
                return;
            }
            float radius = .05f;
            if (type == "BakeryPointLight")
            {
                radius = Mathf.Max(.001f, Get<float>(source, "shadowSpread"));
                int projection = Convert.ToInt32(Get(source, "projMode"));
                if (!forcePoint && (projection == 1 || projection == 4))
                {
                    Set(target, "LightType", 1);
                    Set(target, "Angle", Get<float>(source, "angle") * Mathf.Deg2Rad * .5f);
                    Set(target, "Falloff", Mathf.Clamp01(1 - Get<float>(source, "innerAngle") / 100f));
                    if (projection == 1) { Set(target, "Projection", 2); Set(target, "Cookie", Get(source, "cookie")); }
                }
                else if (!forcePoint && projection == 2) { Set(target, "Projection", 2); Set(target, "Cubemap", Get(source, "cubemap")); }
                Set(target, "Range", Get<float>(source, "cutoff"));
            }
            else
            {
                var renderer = source.GetComponent<Renderer>();
                Vector3 position = renderer != null ? renderer.bounds.center : world.center;
                if (type == "BakeryDirectLight") position -= source.transform.forward * world.extents.magnitude;
                target.transform.position = position;
                radius = Mathf.Max(.05f, renderer != null ? renderer.bounds.extents.magnitude * .25f : world.extents.magnitude * .1f);
                Set(target, "Range", world.size.magnitude * 2);
            }
            Set(target, "LightSourceSize", radius);
            // Parametric PLVs scale emitted power with source area. This is an initial estimate,
            // not a photometric equivalence to Bakery's physical/legacy falloff.
            Set(target, "Intensity", Mathf.Max(0, Get<float>(source, "intensity")) * brightness / (radius * radius));
            Set(target, "Shadows", true); Set(target, "RebakeShadows", true); Set(target, "BakeIntoProbes", false);
        }
        internal static void CopyToBakery(Component source, Component target, bool forcePoint, float brightness)
        {
            target.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            target.transform.localScale = Vector3.one;
            Set(target, "color", Get<Color>(source, "Color"));
            Vector3 scale = source.transform.lossyScale;
            float average = (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3;
            float radius = Mathf.Max(.001f, Get<float>(source, "LightSourceSize") * average);
            Set(target, "intensity", Mathf.Max(0, Get<float>(source, "Intensity")) * radius * radius * brightness);
            Set(target, "shadowSpread", radius); Set(target, "realisticFalloff", true);
            Set(target, "falloffMinRadius", radius); Set(target, "cutoff", Mathf.Sqrt(Mathf.Max(0, Get<float>(source, "SquaredRange"))));
            Set(target, "projMode", 0);
            if (!forcePoint && Get<int>(source, "LightType") == 1)
            {
                Set(target, "angle", Get<float>(source, "Angle") * Mathf.Rad2Deg * 2);
                Set(target, "innerAngle", (1 - Mathf.Clamp01(Get<float>(source, "Falloff"))) * 100);
                Set(target, "projMode", 4);
                if (Get<int>(source, "Projection") == 2)
                {
                    var cookie = Get(source, "Cookie") as Texture2D;
                    if (cookie == null || !Mathf.Approximately(Get<float>(source, "SpotCookieAspect"), 1)) throw new InvalidOperationException("Bakery requires a Texture2D spot cookie with square projection.");
                    Set(target, "cookie", cookie); Set(target, "projMode", 1);
                }
            }
            else if (!forcePoint && Get<int>(source, "Projection") == 2)
            {
                var cube = Get(source, "Cubemap") as Cubemap;
                if (cube == null) throw new InvalidOperationException("Bakery requires a Cubemap asset.");
                Set(target, "cubemap", cube); Set(target, "projMode", 2);
            }
        }
    }
}
