using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lightbulb.AreaLitOcclusion
{
    internal enum AreaLitAuditFix { None, EnableMipmaps, DisableMipmaps, Trilinear, OrderCaptureCamera, ResetMipBias, SceneLayers }

    internal sealed class AreaLitAuditFinding
    {
        public string id;
        public string title;
        public string summary;
        public string detail;
        public string recommendation;
        public Object target;
        public MessageType severity;
        public bool needsDecision;
        public AreaLitAuditFix fix;
        public AreaLitAuditBindingGroup bindings;
        public AreaLitAuditLayerChange layerChange;
        public readonly List<Object> relatedObjects = new List<Object>();
        public float proposedCameraDepth;
        public bool occlusionUvConflict;
    }

    internal sealed class AreaLitAuditReport
    {
        public readonly List<AreaLitAuditFinding> findings = new List<AreaLitAuditFinding>();
        public int scenes;
        public int materials;
        public int emitters;
        public int projectors;
        public int excludedDisabled;
        public readonly List<AreaLitAuditBindingGroup> bindings = new List<AreaLitAuditBindingGroup>();
        public OcclusionUvConflictReport uvConflicts;

        public string Summary => scenes + " scene(s), " + materials + " lighting material(s), " +
                                 emitters + " emitter material slot(s), " + projectors + " projector(s).";
    }

    // Reads the installed shader properties, not AreaLit source or a Bakery dependency.
    // Reports potential camera-visible emitters; it never claims to measure the GPU light table.
    internal static class AreaLitAudit
    {
        private sealed class MaterialUse
        {
            public Material material;
            public bool emitter;
            public bool receiver;
            public bool projector;
        }

        private sealed class EmitterUse
        {
            public Renderer renderer;
            public Material material;
            public int slot;
        }

        public static string BlockedReason
        {
            get
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return "Exit Play Mode before auditing or changing authoring settings.";
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return "Wait for Unity to finish compiling and importing.";
                if (AreaLitOcclusionBakeController.IsRunning || AreaLitOcclusionJournalStore.HasActiveJournal)
                    return "Finish or recover the occlusion transaction before auditing original scenes.";
                for (var i = 0; i < SceneManager.sceneCount; i++)
                    if (AreaLitOcclusionPaths.IsTransactionScene(SceneManager.GetSceneAt(i).path))
                        return "Reopen the original scenes; an occlusion staging scene is loaded.";
                return null;
            }
        }

        public static AreaLitAuditReport Scan(bool includeDisabled)
        {
            var blocked = BlockedReason;
            if (blocked != null) throw new InvalidOperationException(blocked);

            var report = new AreaLitAuditReport();
            var materials = new Dictionary<Material, MaterialUse>();
            var emitters = new List<EmitterUse>();
            var cameras = new List<Camera>();
            var projectors = new List<Projector>();

            // Scene roots exclude prefab-stage previews, unloaded assets and editor utility objects.
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                report.scenes++;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (Hidden(renderer)) continue;
                        var active = renderer.enabled && renderer.gameObject.activeInHierarchy;
                        var slots = renderer.sharedMaterials;
                        for (var slot = 0; slot < slots.Length; slot++)
                        {
                            var use = Classify(slots[slot]);
                            if (use == null) continue;
                            if (!includeDisabled && !active) { report.excludedDisabled++; continue; }
                            materials[use.material] = use;
                            if (use.emitter) emitters.Add(new EmitterUse { renderer = renderer, material = use.material, slot = slot });
                        }
                    }
                    foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                        if (!Hidden(camera) && (includeDisabled || camera.isActiveAndEnabled)) cameras.Add(camera);
                    foreach (var projector in root.GetComponentsInChildren<Projector>(true))
                    {
                        var use = Classify(projector.material);
                        if (Hidden(projector) || use == null || !use.projector) continue;
                        if (!includeDisabled && !projector.isActiveAndEnabled) { report.excludedDisabled++; continue; }
                        materials[use.material] = use;
                        projectors.Add(projector);
                    }
                }
            }

            report.materials = materials.Count;
            report.emitters = emitters.Count;
            report.projectors = projectors.Count;
            var receivers = materials.Values.Where(item => item.receiver).ToList();
            var ordinaryReceivers = receivers.Where(item => !item.emitter).Select(item => item.material).ToList();
            var recursiveCount = receivers.Count(item => item.emitter);
            report.bindings.Add(AreaLitAuditBindings.Collect("_LightMesh", "Mesh", true, receivers.Select(item => item.material)));
            for (var slot = 0; slot < 4; slot++)
                report.bindings.Add(AreaLitAuditBindings.Collect("_LightTex" + slot, slot == 3 ? "Texture 3+" : "Texture " + slot, slot == 0, ordinaryReceivers, recursiveCount));
            report.bindings.Add(AreaLitAuditBindings.Collect("_AreaLitOcclusion", "AreaLit occlusion map", false, ordinaryReceivers));
            var dataTextures = new HashSet<Texture>();
            var lightTextures = new HashSet<Texture>();
            foreach (var use in receivers)
            {
                var data = TextureAt(use.material, "_LightMesh");
                if (data != null) dataTextures.Add(data);
                for (var slot = 0; slot < 4; slot++)
                {
                    var texture = TextureAt(use.material, "_LightTex" + slot);
                    if (texture == null) continue;
                    lightTextures.Add(texture);
                    var expected = slot == 3 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;
                    if (texture.dimension != expected)
                        Add(report, "texture-dimension-" + slot, use.material, "Lighting texture has the wrong dimension",
                            "Texture " + slot + " uses " + texture.dimension + "; this slot expects " + expected + ".",
                            "Assign a texture with the required dimension.", MessageType.Error);
                }
                CheckProjectorMaterial(report, use);
                if (use.emitter) CheckIndirectKeyword(report, use.material);
            }
            foreach (var texture in dataTextures)
            {
                if (lightTextures.Contains(texture))
                    Add(report, "conflicting-texture-roles", texture, "One texture is used as both light data and lighting color",
                        "_LightMesh stores emitter records; _LightTex slots store sampled color. Their mipmap and format requirements differ.",
                        "Separate these assignments before changing this texture's settings.", MessageType.Error);
                CheckDataTexture(report, texture, lightTextures.Contains(texture));
            }
            foreach (var texture in lightTextures)
                if (!dataTextures.Contains(texture)) CheckLightTexture(report, texture);

            foreach (var group in receivers.Where(use => TextureAt(use.material, "_LightMesh") != null)
                         .GroupBy(use => TextureAt(use.material, "_LightMesh")))
                CheckLightingSystem(report, group.Key, group.ToList(), emitters, cameras);
            CheckEmitterProperties(report, emitters);
            AreaLitAuditLayers.AddFindings(report, emitters.Select(use => use.renderer.gameObject), projectors);
            CheckProjectors(report, projectors);
            AreaLitAuditBindings.AddFindings(report);
            report.uvConflicts = AreaLitOcclusionUvTools.ScanConflicts(includeDisabled);
            if (report.uvConflicts.conflicts.Count > 0)
                report.findings.Add(new AreaLitAuditFinding
                {
                    id = "occlusion-uv-conflicts", title = "Occlusion UV conflicts",
                    summary = report.uvConflicts.conflicts.Count + " materials · " + report.uvConflicts.RequiredVariantCount + " variants needed",
                    detail = report.uvConflicts.conflicts.Count + " materials · " + report.uvConflicts.RendererCount +
                             " renderers · " + report.uvConflicts.RequiredVariantCount + " variants needed",
                    recommendation = "Create one material variant per required mapping. The original materials stay unchanged; scene assignments support Undo and tracked revert.",
                    severity = MessageType.Warning, occlusionUvConflict = true
                });
            report.findings.Sort((a, b) =>
            {
                var order = a.needsDecision.CompareTo(b.needsDecision);
                if (order == 0) order = b.severity.CompareTo(a.severity);
                if (order == 0) order = string.Compare(a.title, b.title, StringComparison.Ordinal);
                if (order == 0) order = string.Compare(Describe(a.target), Describe(b.target), StringComparison.Ordinal);
                return order;
            });
            return report;
        }

        private static MaterialUse Classify(Material material)
        {
            if (material == null || material.shader == null) return null;
            var shader = material.shader.name;
            var emitter = shader == "AreaLit/LightMesh";
            var projector = shader == "AreaLit/Projector";
            var native = shader.StartsWith("AreaLit/Standard", StringComparison.Ordinal);
            var integration = material.HasProperty("_AreaLitToggle") && material.HasProperty("_LightMesh") &&
                              material.HasProperty("_LightTex0") && material.GetFloat("_AreaLitToggle") > 0.5f;
            if (!emitter && !projector && !native && !integration) return null;
            return new MaterialUse
            {
                material = material, emitter = emitter, projector = projector,
                receiver = projector || native || integration || (emitter &&
                    (FloatAt(material, "_LightTexPass") > 0.5f || material.IsKeywordEnabled("_LIGHTTEX_PASS")))
            };
        }

        private static void CheckDataTexture(AreaLitAuditReport report, Texture texture, bool conflictingRoles)
        {
            var rt = texture as RenderTexture;
            if (rt == null)
            {
                Add(report, "data-not-rt", texture, "Light data is not a render texture", "The standard AreaLit capture writes emitter records into a render texture.",
                    "Verify whether a custom producer intentionally supplies this texture; otherwise assign the capture target.", MessageType.Warning, true);
                return;
            }
            if (rt.dimension != TextureDimension.Tex2D || rt.width < 6 || rt.height < 1 || rt.height > 63)
                Add(report, "data-size", rt, "Light-data dimensions do not match the standard layout",
                    rt.width + " x " + rt.height + ", " + rt.dimension + ". The standard layout requires a 2D texture at least 6 pixels wide and 1–63 rows.",
                    "Check the installed AreaLit layout before resizing. Height controls polygon capacity, not lighting-image quality.", MessageType.Error);
            if (rt.graphicsFormat != GraphicsFormat.R32G32B32A32_SFloat || rt.sRGB || rt.antiAliasing != 1)
                Add(report, "data-format", rt, "Light-data precision or sampling needs attention",
                    "Format: " + rt.graphicsFormat + "; sRGB: " + rt.sRGB + "; samples: " + rt.antiAliasing + ".",
                    "Standard AreaLit uses linear RGBA 32-bit float data with one sample. Verify the data contract before changing format.", MessageType.Warning);
            if (rt.depthStencilFormat != GraphicsFormat.D24_UNorm_S8_UInt &&
                rt.depthStencilFormat != GraphicsFormat.D32_SFloat_S8_UInt && rt.depthStencilFormat != GraphicsFormat.S8_UInt)
                Add(report, "data-stencil", rt, "Light-data capture needs a stencil buffer",
                    "Depth/stencil format: " + rt.depthStencilFormat + ". Standard AreaLit uses stencil to pack polygon records.",
                    "Select a platform-supported depth/stencil format with an 8-bit stencil component.", MessageType.Error);
            if (rt.useMipMap)
                Add(report, "data-mips", rt, "Unused mipmaps on the light-data texture", "Emitter records are read directly, not filtered as a lighting image.",
                    "Disable data-texture mipmaps; retain lighting-color mipmaps.", MessageType.Warning, false,
                    conflictingRoles ? AreaLitAuditFix.None : AreaLitAuditFix.DisableMipmaps);
        }

        private static void CheckLightTexture(AreaLitAuditReport report, Texture texture)
        {
            var rt = texture as RenderTexture;
            var hasMips = rt != null ? rt.useMipMap : texture.mipmapCount > 1;
            if (!hasMips && (texture.width > 1 || texture.height > 1))
                Add(report, "radiance-mips", texture, "Lighting-color mipmaps are missing", "AreaLit uses the mip chain to filter textured illumination and reflections.",
                    "Enable mipmaps. Render-texture fixes also enable automatic mip generation and recreate a live buffer.", MessageType.Warning, false,
                    rt != null ? AreaLitAuditFix.EnableMipmaps : AreaLitAuditFix.None);
            if (rt != null && rt.useMipMap && !rt.autoGenerateMips)
                Add(report, "manual-mips", rt, "Verify manual mipmap generation", "Mipmaps are enabled but automatic generation is off.",
                    "Confirm that the texture producer generates the full mip chain after updates. Manual generation may be intentional.", MessageType.Info, true);
            if (texture.filterMode != FilterMode.Trilinear)
                Add(report, "radiance-filter", texture, "Lighting texture is not trilinear", "Current filter: " + texture.filterMode + ". Texture-controlled specular sampling uses this setting.",
                    "Use trilinear filtering. Existing anisotropy will be preserved; hardcoded diffuse samplers are unaffected.", MessageType.Warning, false, AreaLitAuditFix.Trilinear);
            if (Mathf.Abs(texture.mipMapBias) > 0.01f)
            {
                var finding = Add(report, "mip-bias", texture, "Custom mip bias",
                    texture.name + ": " + texture.mipMapBias.ToString("0.##", CultureInfo.InvariantCulture) + ". Non-default; may be intentional.",
                    "Negative bias sharpens reflections. Reset to 0 only if you want the default.", MessageType.Info, false, AreaLitAuditFix.ResetMipBias);
                finding.summary = finding.detail;
            }
        }

        private static void CheckLightingSystem(AreaLitAuditReport report, Texture data, List<MaterialUse> receivers,
            List<EmitterUse> allEmitters, List<Camera> cameras)
        {
            var captures = cameras.Where(camera => camera.targetTexture == data).ToList();
            if (captures.Count == 0)
                Add(report, "missing-capture", data, "No capture camera found for this light-data texture",
                    "No camera in the scan scope targets this texture. A custom or runtime producer may exist.",
                    "Check the capture camera's target and enabled state, or verify the custom producer.", MessageType.Warning, true);
            if (captures.Count > 1)
                Add(report, "multiple-captures", data, "Multiple cameras write the same light-data texture",
                    string.Join("\n", captures.Select(Describe)), "Confirm which camera owns this light table. Later captures may replace earlier records.", MessageType.Warning, true);
            foreach (var camera in captures)
            {
                if (!camera.orthographic)
                    Add(report, "capture-projection", camera, "Emitter-data camera is not orthographic", "The standard emitter capture only runs for orthographic cameras.",
                        "Configure this capture as orthographic and review its coverage.", MessageType.Error);
                if (camera.renderingPath != RenderingPath.VertexLit)
                    Add(report, "capture-path", camera, "Verify the emitter-data camera rendering path",
                        "Configured path: " + camera.renderingPath + ". The standard LightMesh capture pass uses VertexLit.",
                        "Use VertexLit for the standard capture, or verify the custom replacement-rendering path.", MessageType.Warning, true);
            }
            var emitters = allEmitters.Where(emitter => captures.Any(camera => CanSee(camera, emitter.renderer))).ToList();
            CheckBudget(report, data, emitters);
            var bounces = receivers.Where(use => use.emitter)
                .SelectMany(use => CheckBounceCapture(report, use.material, emitters, captures, cameras)).Distinct().ToList();
            if (captures.Count == 1 && bounces.Count > 0 && captures[0].depth >= bounces.Min(camera => camera.depth))
            {
                var proposed = bounces.Min(camera => camera.depth) - 1f;
                var finding = Add(report, "capture-order", captures[0], "Capture order needs attention",
                    "Data depth " + captures[0].depth + ", bounce depth " + bounces.Min(camera => camera.depth) +
                    ". Data must render first.",
                    "The order is Light Mesh capture, then indirect-bounce capture, then the player view. Set this data camera to " + proposed +
                    "; leave the bounce and player cameras unchanged. Runtime player/copy scheduling still needs an in-client check.",
                    MessageType.Warning, false, AreaLitAuditFix.OrderCaptureCamera);
                finding.proposedCameraDepth = proposed;
                finding.summary = finding.detail;
                finding.relatedObjects.AddRange(bounces);
            }
        }

        private static List<Camera> CheckBounceCapture(AreaLitAuditReport report, Material material, List<EmitterUse> emitters,
            List<Camera> dataCameras, List<Camera> cameras)
        {
            var linkedBounces = new List<Camera>();
            var users = emitters.Where(emitter => emitter.material == material).ToList();
            if (!material.IsKeywordEnabled("_LIGHTTEX_PASS")) return linkedBounces;
            foreach (var camera in cameras.Where(camera => camera.targetTexture != null && !dataCameras.Contains(camera) &&
                         camera.orthographic && camera.actualRenderingPath != RenderingPath.VertexLit && users.Any(use => CanSee(camera, use.renderer))))
            {
                // The standard ForwardBase Tex pass writes UV-space lighting in orthographic
                // cameras even with no recursive input. Single-bounce capture still needs data first.
                linkedBounces.Add(camera);
                for (var slot = 0; slot < 3; slot++)
                {
                    var input = TextureAt(material, "_LightTex" + slot);
                    if (input == camera.targetTexture)
                    {
                        Add(report, "capture-feedback-" + camera.GetInstanceID() + "-" + slot, material,
                            "Proxy may sample its current capture target", "Texture " + slot + " is also the target of " + Describe(camera) + ".",
                            "Use a separate previous-frame copy for recursive input, or remove the recursive input for single-bounce operation.", MessageType.Error);
                    }
                }
            }
            return linkedBounces;
        }

        private static void CheckBudget(AreaLitAuditReport report, Texture data, List<EmitterUse> emitters)
        {
            long triangleUpperBound = 0;
            var unknown = 0;
            foreach (var emitter in emitters)
            {
                var filter = emitter.renderer.GetComponent<MeshFilter>();
                var skinned = emitter.renderer as SkinnedMeshRenderer;
                var mesh = skinned != null ? skinned.sharedMesh : filter != null ? filter.sharedMesh : null;
                if (mesh == null || mesh.subMeshCount == 0) { unknown++; continue; }
                var submesh = Mathf.Min(emitter.slot, mesh.subMeshCount - 1);
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles) { unknown++; continue; }
                var triangles = (long)mesh.GetIndexCount(submesh) / 3;
                triangleUpperBound += triangles * (FloatAt(emitter.material, "_Cull", 2) == 0 ? 2 : 1);
                if (triangles > 256)
                    Add(report, "primitive-limit-" + emitter.slot, emitter.renderer, "Emitter submesh exceeds the standard primitive limit",
                        "Material slot " + emitter.slot + " has " + triangles + " triangles. Standard AreaLit processes only the first 256 primitives in a draw.",
                        "Use a deliberately simplified emitter mesh; raising the light-table height does not remove this per-draw limit.", MessageType.Warning);
            }
            if (triangleUpperBound > data.height || unknown > 0)
                Add(report, "budget", data, "Review potential emitter-table pressure",
                    "Capacity: " + data.height + " rows. Potentially visible material slots: " + emitters.Count +
                    ". Raw triangle-side upper bound: " + triangleUpperBound + "; unknown geometry: " + unknown + ".",
                    "Inspect the live table before resizing. Quad merging, degeneracy, culling, animation and platform behavior change actual occupancy; this is not a measured overflow.", MessageType.Info, true);
        }

        private static void CheckEmitterProperties(AreaLitAuditReport report, List<EmitterUse> emitters)
        {
            foreach (var material in emitters.Select(use => use.material).Distinct())
            {
                var index = FloatAt(material, "_LightTexIndex");
                if (float.IsNaN(index) || float.IsInfinity(index) || index < -1 || Mathf.Abs(index - Mathf.Round(index)) > 0.001f)
                    Add(report, "emitter-index", material, "Emitter texture index is invalid", "Texture index: " + index + ".",
                        "Use -1 for an untextured emitter, 0–2 for dedicated slots, or 3 and above for array slices.", MessageType.Error);
                var topology = FloatAt(material, "_LightTopology");
                if (topology != 0 && topology != 3 && topology != 4)
                    Add(report, "emitter-topology", material, "Emitter topology setting is invalid", "Topology value: " + topology + ".",
                        "Choose Auto, Triangle or Quad in the emitter inspector.", MessageType.Error);
                CheckIndirectKeyword(report, material);
            }
        }

        private static void CheckIndirectKeyword(AreaLitAuditReport report, Material material)
        {
            if ((FloatAt(material, "_LightTexPass") > 0.5f) == material.IsKeywordEnabled("_LIGHTTEX_PASS")) return;
            Add(report, "indirect-keyword", material, "Indirect-light toggle and shader keyword disagree",
                "The saved _LightTexPass value does not match _LIGHTTEX_PASS.",
                "Set the intended Indirect Light state using the emitter inspector; do not infer intent from only one saved value.", MessageType.Warning);
        }

        private static void CheckProjectorMaterial(AreaLitAuditReport report, MaterialUse use)
        {
            if (!use.projector || !use.material.HasProperty("_ProjectorColor")) return;
            var color = use.material.GetColor("_ProjectorColor");
            var retained = 1f - color.a;
            var specularOff = use.material.IsKeywordEnabled("_SPECULARHIGHLIGHTS_OFF");
            var finding = Add(report, "projector-policy", use.material, "Projector appearance (optional)",
                "On: adds AreaLit highlights and GPU work; matte avatar surfaces can look glossy.\n" +
                "Off: keeps diffuse AreaLit lighting, but adds no AreaLit highlights and skips that specular work.\n" +
                "Highlights use this projector material's Smoothness/Metallic, not the avatar's settings. The avatar's own specular settings stay unchanged.",
                "Select this material below. In the Inspector, under Forward Rendering Options, check Specular Highlights for added shine; " +
                "uncheck it for diffuse-only projection. This is not Glossy Reflections. Neither choice is an error.\n\n" +
                "Brightness: AreaLit Projector > Color > A (currently " + color.a.ToString("0.###", CultureInfo.InvariantCulture) + "). " +
                "Lower A preserves more existing avatar brightness but reduces light/dark contrast. Higher A deepens unlit areas but can make dim avatars too dark. " +
                "Color's RGB controls the AreaLit intensity/tint.", MessageType.Info, true);
            finding.summary = "Specular Highlights: " + (specularOff ? "Off" : "On") + ". Optional—not an error.\n" +
                "At zero projector contribution: approximately " + retained.ToString("P0", CultureInfo.InvariantCulture) + " of existing color remains.";
        }

        private static void CheckProjectors(AreaLitAuditReport report, List<Projector> projectors)
        {
            for (var i = 0; i < projectors.Count; i++)
                for (var j = i + 1; j < projectors.Count; j++)
                {
                    var overlap = ~(projectors[i].ignoreLayers | projectors[j].ignoreLayers);
                    if (overlap == 0) continue;
                    var layers = Enumerable.Range(0, 32).Where(layer => (overlap & (1 << layer)) != 0)
                        .Select(layer => layer + ": " + (string.IsNullOrEmpty(LayerMask.LayerToName(layer)) ? "unnamed" : LayerMask.LayerToName(layer)));
                    Add(report, "projector-overlap-" + projectors[j].GetInstanceID(), projectors[i], "Projector receiver layers overlap",
                        "Also targeted by " + Describe(projectors[j]) + ": " + string.Join(", ", layers) + ".",
                        "Review receiving layers and spatial/camera coverage. Shared layer bits alone do not prove double application.", MessageType.Info, true);
                }
        }

        private static bool CanSee(Camera camera, Renderer renderer)
        {
            return (camera.cullingMask & (1 << renderer.gameObject.layer)) != 0 &&
                   GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(camera), renderer.bounds);
        }

        private static bool Hidden(Component component) => (component.hideFlags & HideFlags.HideAndDontSave) != 0 ||
            (component.gameObject.hideFlags & HideFlags.HideAndDontSave) != 0;

        private static Texture TextureAt(Material material, string property) => material.HasProperty(property) ? material.GetTexture(property) : null;
        private static float FloatAt(Material material, string property, float defaultValue = 0) => material.HasProperty(property) ? material.GetFloat(property) : defaultValue;

        public static string Describe(Object target)
        {
            if (target == null) return "Unavailable object";
            var component = target as Component;
            if (component != null) return AreaLitOcclusionDiscovery.GetDisplayPath(component.transform) + " (" + component.GetType().Name + ")";
            var gameObject = target as GameObject;
            if (gameObject != null) return AreaLitOcclusionDiscovery.GetDisplayPath(gameObject.transform) + " (GameObject)";
            var path = AssetDatabase.GetAssetPath(target);
            return string.IsNullOrEmpty(path) ? target.name + " (unsaved object)" : path;
        }

        private static AreaLitAuditFinding Add(AreaLitAuditReport report, string rule, Object target, string title, string detail,
            string recommendation, MessageType severity, bool decision = false, AreaLitAuditFix fix = AreaLitAuditFix.None)
        {
            var id = rule + ":" + target.GetInstanceID();
            var existing = report.findings.FirstOrDefault(item => item.id == id);
            if (existing != null) return existing;
            var finding = new AreaLitAuditFinding
            {
                id = id, target = target, title = title, summary = target.name, detail = detail, recommendation = recommendation,
                severity = severity, needsDecision = decision, fix = fix
            };
            report.findings.Add(finding);
            return finding;
        }
    }
}
