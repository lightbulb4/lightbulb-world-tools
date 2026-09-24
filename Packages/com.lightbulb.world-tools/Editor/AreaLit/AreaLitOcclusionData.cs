using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.AreaLitOcclusion
{
    internal enum OcclusionChannel
    {
        Red = 0,
        Green = 1,
        Blue = 2,
        Alpha = 3
    }

    [Serializable]
    internal sealed class ObjectLocator
    {
        public string sourceScenePath;
        public string siblingPath;
        public string componentType;
        public int componentIndex;
    }

    internal sealed class EmitterCandidate
    {
        public Renderer renderer;
        public ObjectLocator rendererLocator;
        public Material material;
        public int materialSlot;
        public OcclusionChannel channel;
        public bool selected;
        public string selectionReason;
        public string hierarchyPath;
        public Mesh sourceMesh;
        public int sourceSubmesh;
        public bool canGenerateProxy;
        public bool canAutoProxy;
        public string proxyStatus;
        public float automaticBakeIntensity;
        public float bakeIntensity;
        public bool intensityOverridden;
    }

    internal sealed class ReceiverCandidate
    {
        public Material material;
        public string assetPath;
        public bool selected;
        public bool areaLitEnabled;
        public string selectionReason;
        public int rendererCount;
    }

    internal sealed class DiscoveryResult
    {
        public readonly List<EmitterCandidate> emitters = new List<EmitterCandidate>();
        public readonly List<ReceiverCandidate> receivers = new List<ReceiverCandidate>();
        public readonly List<string> warnings = new List<string>();
    }

    internal static class AreaLitOcclusionPaths
    {
        public const string RootAssetPath = "Assets/Lightbulb/AreaLitOcclusion";
        public const string TransactionsAssetPath = RootAssetPath + "/Transactions";
        public const string GeneratedAssetPath = RootAssetPath + "/Generated";

        public static bool IsTransactionScene(string scenePath)
        {
            return !string.IsNullOrEmpty(scenePath) &&
                   scenePath.Replace('\\', '/').StartsWith(TransactionsAssetPath + "/", StringComparison.OrdinalIgnoreCase);
        }

        public static string ProjectRoot
        {
            get { return Directory.GetParent(Application.dataPath).FullName; }
        }

        public static string ToAbsolutePath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        public static string GetGeneratedFolder(string scenePath)
        {
            var sceneGuid = AssetDatabase.AssetPathToGUID(scenePath);
            if (string.IsNullOrEmpty(sceneGuid))
            {
                throw new InvalidOperationException("The active scene must be saved before an occlusion output folder can be assigned.");
            }

            // The GUID makes the path stable even if the scene is renamed or moved.
            return GeneratedAssetPath + "/" + sceneGuid;
        }

        public static string CreateTransactionId()
        {
            return DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unnamed";

            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars).Trim();
        }

        public static void EnsureAssetFolder(string assetFolder)
        {
            assetFolder = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            if (!assetFolder.StartsWith("Assets", StringComparison.Ordinal))
            {
                throw new ArgumentException("Unity asset folders must be inside Assets.", "assetFolder");
            }

            var parts = assetFolder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    var guid = AssetDatabase.CreateFolder(current, parts[i]);
                    if (string.IsNullOrEmpty(guid))
                    {
                        throw new IOException("Unity could not create asset folder: " + next);
                    }
                }

                current = next;
            }
        }
    }

    internal static class AreaLitOcclusionDiscovery
    {
        private const string EmitterShaderName = "AreaLit/LightMesh";
        private const string EmitterChannelProperty = "_LightChannel";
        private const string EmitterMeshProperty = "_LightMesh";
        private const string EmitterColorProperty = "_LightColor";
        private const string EmitterIntensityProperty = "_LightIntensity";
        private const string ReceiverTextureProperty = "_AreaLitOcclusion";
        private const string ReceiverToggleProperty = "_AreaLitToggle";

        public static DiscoveryResult ScanLoadedScenes()
        {
            var result = new DiscoveryResult();
            ScanEmitters(result);
            ScanReceivers(result);
            return result;
        }

        private static void ScanEmitters(DiscoveryResult result)
        {
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            {
                if (!IsLoadedSceneObject(renderer)) continue;

                var materials = renderer.sharedMaterials;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (material == null || material.shader == null) continue;
                    var exactShader = string.Equals(material.shader.name, EmitterShaderName, StringComparison.Ordinal);
                    var compatibleSignature = material.HasProperty(EmitterChannelProperty) &&
                                              material.HasProperty(EmitterMeshProperty) &&
                                              material.HasProperty(EmitterColorProperty) &&
                                              material.HasProperty(EmitterIntensityProperty);
                    if (!exactShader && !compatibleSignature) continue;

                    var channel = OcclusionChannel.Red;
                    if (material.HasProperty(EmitterChannelProperty))
                    {
                        channel = (OcclusionChannel)Mathf.Clamp(
                            Mathf.RoundToInt(material.GetFloat(EmitterChannelProperty)), 0, 3);
                    }

                    var sourceMesh = GetSourceMesh(renderer);
                    var canGenerateProxy = renderer is MeshRenderer && sourceMesh != null &&
                                           sourceMesh.subMeshCount > 0;
                    var active = renderer.gameObject.activeInHierarchy && renderer.enabled;
                    var intensity = material.GetFloat(EmitterIntensityProperty);
                    var color = material.GetColor(EmitterColorProperty);
                    var automaticIntensity = NormalizeBakeIntensity(
                        Mathf.Max(color.r, Mathf.Max(color.g, color.b)) * Mathf.Max(0f, intensity),
                        1f);
                    result.emitters.Add(new EmitterCandidate
                    {
                        renderer = renderer,
                        rendererLocator = CreateLocator(renderer),
                        material = material,
                        materialSlot = slot,
                        channel = channel,
                        selected = active && canGenerateProxy,
                        selectionReason = !active
                            ? "Off · inactive emitter"
                            : canGenerateProxy ? "On · detected AreaLit emitter" : "Off · proxy geometry unavailable",
                        hierarchyPath = GetDisplayPath(renderer.transform),
                        sourceMesh = sourceMesh,
                        sourceSubmesh = sourceMesh == null || sourceMesh.subMeshCount == 0
                            ? -1
                            : Mathf.Min(slot, sourceMesh.subMeshCount - 1),
                        canGenerateProxy = canGenerateProxy,
                        canAutoProxy = canGenerateProxy,
                        proxyStatus = canGenerateProxy
                            ? "Exact emitter geometry · automatic proxy settings"
                            : "Blocked · needs MeshRenderer geometry with a MeshFilter",
                        automaticBakeIntensity = automaticIntensity,
                        bakeIntensity = automaticIntensity
                    });
                }
            }

            result.emitters.Sort((a, b) => string.Compare(a.hierarchyPath, b.hierarchyPath, StringComparison.OrdinalIgnoreCase));
            foreach (var emitter in result.emitters.Where(item => !item.canAutoProxy))
            {
                result.warnings.Add(
                    "AreaLit emitter '" + emitter.hierarchyPath + "' cannot be proxied safely. " +
                    "Use a MeshRenderer with a MeshFilter.");
            }
        }

        private static Mesh GetSourceMesh(Renderer renderer)
        {
            var meshRenderer = renderer as MeshRenderer;
            if (meshRenderer != null)
            {
                var filter = meshRenderer.GetComponent<MeshFilter>();
                return filter == null ? null : filter.sharedMesh;
            }

            var skinned = renderer as SkinnedMeshRenderer;
            return skinned == null ? null : skinned.sharedMesh;
        }

        public static float NormalizeBakeIntensity(float value, float fallback = 1f)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) value = fallback;
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 1f;
            return Mathf.Max(0f, value);
        }

        private static void ScanReceivers(DiscoveryResult result)
        {
            var byPath = new Dictionary<string, ReceiverCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            {
                if (!IsLoadedSceneObject(renderer)) continue;

                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || !material.HasProperty(ReceiverTextureProperty)) continue;
                    var assetPath = AssetDatabase.GetAssetPath(material);
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        result.warnings.Add("Receiver material '" + material.name + "' is not a saved asset and cannot be updated safely.");
                        continue;
                    }

                    ReceiverCandidate candidate;
                    if (!byPath.TryGetValue(assetPath, out candidate))
                    {
                        var hasToggle = material.HasProperty(ReceiverToggleProperty);
                        var areaLitEnabled = !hasToggle || material.GetFloat(ReceiverToggleProperty) > 0.5f;
                        candidate = new ReceiverCandidate
                        {
                            material = material,
                            assetPath = assetPath,
                            selected = areaLitEnabled,
                            areaLitEnabled = areaLitEnabled,
                            selectionReason = !hasToggle
                                ? "On · compatible shader"
                                : areaLitEnabled ? "On · AreaLit enabled" : "Off · AreaLit disabled",
                            rendererCount = 0
                        };
                        byPath.Add(assetPath, candidate);
                    }

                    candidate.rendererCount++;
                }
            }

            result.receivers.AddRange(byPath.Values.OrderBy(candidate => candidate.assetPath, StringComparer.OrdinalIgnoreCase));
        }

        public static ObjectLocator CreateLocator(Component component)
        {
            var sameType = component.gameObject.GetComponents(component.GetType());
            var componentIndex = Array.IndexOf(sameType, component);
            if (componentIndex < 0) throw new InvalidOperationException("Could not locate component on " + component.name);

            var indices = new List<int>();
            var current = component.transform;
            while (current != null)
            {
                indices.Add(current.GetSiblingIndex());
                current = current.parent;
            }
            indices.Reverse();

            return new ObjectLocator
            {
                sourceScenePath = component.gameObject.scene.path,
                siblingPath = string.Join("/", indices.Select(index => index.ToString()).ToArray()),
                componentType = component.GetType().AssemblyQualifiedName,
                componentIndex = componentIndex
            };
        }

        public static Component ResolveLocator(Scene scene, ObjectLocator locator)
        {
            if (!scene.IsValid() || !scene.isLoaded || locator == null) return null;

            var indices = locator.siblingPath.Split('/').Select(int.Parse).ToArray();
            if (indices.Length == 0) return null;
            var roots = scene.GetRootGameObjects();
            if (indices[0] < 0 || indices[0] >= roots.Length) return null;

            var transform = roots[indices[0]].transform;
            for (var i = 1; i < indices.Length; i++)
            {
                if (indices[i] < 0 || indices[i] >= transform.childCount) return null;
                transform = transform.GetChild(indices[i]);
            }

            var type = Type.GetType(locator.componentType);
            if (type == null) return null;
            var components = transform.gameObject.GetComponents(type);
            if (locator.componentIndex < 0 || locator.componentIndex >= components.Length) return null;
            return components[locator.componentIndex];
        }

        public static string GetDisplayPath(Transform transform)
        {
            var names = new List<string>();
            var current = transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return transform.gameObject.scene.name + "/" + string.Join("/", names.ToArray());
        }

        private static bool IsLoadedSceneObject(Component component)
        {
            if (component == null || component.gameObject == null) return false;
            var scene = component.gameObject.scene;
            return scene.IsValid() && scene.isLoaded && (component.hideFlags & HideFlags.HideAndDontSave) == 0;
        }
    }
}
