using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lightbulb.AreaLitOcclusion.Tests")]

namespace Lightbulb.AreaLitOcclusion
{
    internal static class AreaLitOcclusionBakeController
    {
        private const string ReceiverTextureProperty = "_AreaLitOcclusion";
        private const string EmitterColorProperty = "_LightColor";
        private const string EmitterIntensityProperty = "_LightIntensity";
        private const string EmitterChannelProperty = "_LightChannel";

        private static AreaLitOcclusionJournal activeJournal;
        private static bool monitoring;
        private static bool bakeWasObserved;
        private static bool completionReceived;

        public static event Action StateChanged;

        public static string LastStatus { get; private set; }
        public static bool IsRunning { get { return activeJournal != null; } }
        public static bool IsPreparedForInspection
        {
            get
            {
                return activeJournal != null &&
                       string.Equals(activeJournal.state, "InspectionReady", StringComparison.Ordinal);
            }
        }

        internal sealed class BakedReceiver
        {
            public ObjectLocator locator;
            public int slot;
            public string materialPath, sourcePath;
            public Vector4 scaleOffset;
        }

        internal sealed class BakeOutputPlan
        {
            public readonly HashSet<string> sourceTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<BakedReceiver> receivers = new List<BakedReceiver>();
            public readonly List<string> warnings = new List<string>();
        }

        public static void BeginBake(
            DiscoveryResult discovery,
            bool applyToMaterials,
            float outputBrightness = 1f,
            float outputContrast = 1f,
            float bakeIntensityMultiplier = 1f)
        {
            BeginTransaction(discovery, applyToMaterials, outputBrightness, outputContrast, bakeIntensityMultiplier, true);
        }

        public static void PrepareForInspection(
            DiscoveryResult discovery,
            bool applyToMaterials,
            float outputBrightness = 1f,
            float outputContrast = 1f,
            float bakeIntensityMultiplier = 1f)
        {
            BeginTransaction(discovery, applyToMaterials, outputBrightness, outputContrast, bakeIntensityMultiplier, false);
        }

        private static void BeginTransaction(
            DiscoveryResult discovery,
            bool applyToMaterials,
            float outputBrightness,
            float outputContrast,
            float bakeIntensityMultiplier,
            bool startBakeImmediately)
        {
            if (discovery == null) throw new ArgumentNullException("discovery");
            ValidateCanBegin(discovery, applyToMaterials);

            var activeScene = SceneManager.GetActiveScene();
            var transactionId = AreaLitOcclusionPaths.CreateTransactionId();
            var transactionAssetPath = AreaLitOcclusionPaths.TransactionsAssetPath + "/" + transactionId;
            var bakeOutputAssetPath = transactionAssetPath + "/BakeOutput";
            var generatedOutputAssetPath = AreaLitOcclusionPaths.GetGeneratedFolder(activeScene.path);

            var journal = new AreaLitOcclusionJournal
            {
                transactionId = transactionId,
                createdUtc = DateTime.UtcNow.ToString("O"),
                state = "Preparing",
                activeSourceScenePath = activeScene.path,
                transactionAssetPath = transactionAssetPath,
                bakeOutputAssetPath = bakeOutputAssetPath,
                generatedOutputAssetPath = generatedOutputAssetPath,
                applyToMaterials = applyToMaterials,
                outputAdjustmentsCaptured = true,
                outputBrightness = AreaLitOcclusionImageAdjuster.NormalizeBrightness(outputBrightness),
                outputContrast = AreaLitOcclusionImageAdjuster.NormalizeContrast(outputContrast),
                bakeIntensityMultiplierCaptured = true,
                bakeIntensityMultiplier = AreaLitOcclusionDiscovery.NormalizeBakeIntensity(bakeIntensityMultiplier, 1f)
            };

            // Capture the original scene's Bakery editor state before opening any staging scene.
            // The Bakery window derives its default output path from the currently active scene.
            var originalBakeryWindow = AreaLitOcclusionBakery.GetOrOpenRenderWindow();
            AreaLitOcclusionBakery.LoadRenderSettings(originalBakeryWindow);
            var projectSettings = AreaLitOcclusionBakery.GetProjectSettings();
            journal.previousBakeryOutputPath = AreaLitOcclusionBakery.OutputPath;
            journal.previousBakeryUseScenePath = AreaLitOcclusionBakery.UseScenePath;
            journal.previousDeletePreviousLightmaps =
                AreaLitOcclusionBakery.GetDeletePreviousLightmaps(projectSettings);
            journal.bakerySettingsCaptured = true;

            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
            {
                journal.originalSceneSetup.Add(new JournalSceneSetup
                {
                    path = setup.path,
                    isLoaded = setup.isLoaded,
                    isActive = setup.isActive
                });
            }

            foreach (var emitter in discovery.emitters)
            {
                journal.emitters.Add(new JournalEmitter
                {
                    rendererLocator = emitter.rendererLocator,
                    materialSlot = emitter.materialSlot,
                    sourceSubmesh = emitter.sourceSubmesh,
                    rendererWasEnabled = emitter.renderer != null && emitter.renderer.enabled,
                    selected = emitter.selected,
                    channel = emitter.channel,
                    bakeIntensity = AreaLitOcclusionDiscovery.NormalizeBakeIntensity(emitter.bakeIntensity)
                });
            }

            journal.receiverMaterialPaths.AddRange(
                discovery.receivers.Where(receiver => receiver.selected).Select(receiver => receiver.assetPath));

            activeJournal = journal;
            AreaLitOcclusionJournalStore.Save(journal);
            Notify("Creating isolated staging scenes...");

            try
            {
                PrepareStagingScenes(journal);
                OpenAndMutateStagingScenes(journal);
                if (startBakeImmediately)
                {
                    StartBakery(journal);
                }
                else
                {
                    ConfigureBakeryForTransaction(journal);
                    journal.state = "InspectionReady";
                    AreaLitOcclusionJournalStore.Save(journal);
                    completionReceived = false;
                    bakeWasObserved = false;
                    SubscribeToBakery();
                    Notify("Inspection mode is ready. Only isolated staging scene copies are open.");
                }
            }
            catch (Exception exception)
            {
                var context = startBakeImmediately
                    ? "The occlusion bake could not start."
                    : "The inspection scene could not be prepared.";
                FailAndRecover(journal, context, exception);
                throw;
            }
        }

        public static bool TryResumePrepared(AreaLitOcclusionJournal journal)
        {
            if (journal == null || !string.Equals(journal.state, "InspectionReady", StringComparison.Ordinal))
            {
                return false;
            }

            var stagingPaths = new HashSet<string>(
                journal.sceneCopies.Select(copy => copy.stagingPath),
                StringComparer.OrdinalIgnoreCase);
            if (stagingPaths.Count == 0) return false;

            for (var index = 0; index < journal.sceneCopies.Count; index++)
            {
                var scene = SceneManager.GetSceneByPath(journal.sceneCopies[index].stagingPath);
                if (!scene.IsValid() || !scene.isLoaded) return false;
            }

            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.isLoaded && !stagingPaths.Contains(scene.path)) return false;
            }

            try
            {
                ConfigureBakeryForTransaction(journal);
            }
            catch (Exception exception)
            {
                Debug.LogError("[AreaLit Occlusion] Could not restore the protected Bakery inspection settings.\n" + exception);
                return false;
            }

            activeJournal = journal;
            completionReceived = false;
            bakeWasObserved = false;
            SubscribeToBakery();
            Notify("Inspection mode resumed. Only isolated staging scene copies are open.");
            return true;
        }

        public static void StartPreparedBake()
        {
            var journal = activeJournal ?? AreaLitOcclusionJournalStore.LoadActive();
            if (journal == null || !string.Equals(journal.state, "InspectionReady", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("No prepared occlusion inspection is active.");
            }
            if (!TryResumePrepared(journal))
            {
                throw new InvalidOperationException("The prepared staging scenes are no longer the only loaded scenes. Revert safely, then prepare them again.");
            }
            if (AreaLitOcclusionBakery.BakeInProgress)
            {
                throw new InvalidOperationException("Bakery is already rendering.");
            }

            try
            {
                StartBakery(journal);
            }
            catch (Exception exception)
            {
                FailAndRecover(journal, "The prepared occlusion bake could not start.", exception);
                throw;
            }
        }

        public static void RevertPreparedScene()
        {
            var journal = activeJournal ?? AreaLitOcclusionJournalStore.LoadActive();
            if (journal == null || !string.Equals(journal.state, "InspectionReady", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("No prepared occlusion inspection is active.");
            }

            CancelAndRestore(journal, "Prepared inspection reverted without baking.");
        }

        public static void ResumeMonitoring(AreaLitOcclusionJournal journal)
        {
            if (journal == null || monitoring) return;
            activeJournal = journal;
            completionReceived = false;
            bakeWasObserved = AreaLitOcclusionBakery.BakeInProgress;
            SubscribeToBakery();
            Notify("Reattached to the active Bakery occlusion bake.");
        }

        private static void ValidateCanBegin(DiscoveryResult discovery, bool applyToMaterials)
        {
            AreaLitOcclusionBakery.RequireAvailable();
            if (AreaLitOcclusionJournalStore.HasActiveJournal)
            {
                throw new InvalidOperationException("An earlier occlusion transaction needs recovery before another bake can start.");
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("Exit Play Mode before baking occlusion.");
            }
            if (AreaLitOcclusionBakery.BakeInProgress)
            {
                throw new InvalidOperationException("Bakery is already rendering.");
            }
            if (!discovery.emitters.Any(emitter => emitter.selected))
            {
                throw new InvalidOperationException("Select at least one AreaLit emitter.");
            }
            if (discovery.emitters.Any(emitter => emitter.selected && !emitter.canAutoProxy))
            {
                throw new InvalidOperationException("Every selected AreaLit emitter must have safe proxy geometry.");
            }
            if (discovery.emitters.Any(emitter => emitter.selected && emitter.channel == OcclusionChannel.Alpha))
            {
                throw new InvalidOperationException("The color-lightmap workflow can bake red, green, and blue channels only. Alpha requires a shadowmask-based output mode.");
            }
            // UVs are always applied to selected receivers, even when texture assignment is off.
            {
                var dirtyMaterial = discovery.receivers
                    .Where(receiver => receiver.selected)
                    .Select(receiver => receiver.material)
                    .FirstOrDefault(material => material != null && EditorUtility.IsDirty(material));
                if (dirtyMaterial != null)
                {
                    throw new InvalidOperationException(
                        "Receiver material '" + dirtyMaterial.name + "' has unsaved changes. Save it or deselect it before baking.");
                }
            }

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                if (AreaLitOcclusionPaths.IsTransactionScene(scene.path))
                {
                    throw new InvalidOperationException(
                        "A retained AreaLit Occlusion staging scene is loaded. Open the original scene before starting a new transaction.");
                }
                if (string.IsNullOrEmpty(scene.path))
                {
                    throw new InvalidOperationException("Every loaded scene must be saved before a safe staging copy can be created.");
                }
                if (scene.isDirty)
                {
                    throw new InvalidOperationException("Scene '" + scene.name + "' has unsaved changes. Save it before starting; the tool will never auto-save user scenes.");
                }
            }
        }

        private static void PrepareStagingScenes(AreaLitOcclusionJournal journal)
        {
            AreaLitOcclusionPaths.EnsureAssetFolder(AreaLitOcclusionPaths.RootAssetPath);
            AreaLitOcclusionPaths.EnsureAssetFolder(AreaLitOcclusionPaths.TransactionsAssetPath);
            AreaLitOcclusionPaths.EnsureAssetFolder(journal.transactionAssetPath);
            AreaLitOcclusionPaths.EnsureAssetFolder(journal.transactionAssetPath + "/Scenes");
            AreaLitOcclusionPaths.EnsureAssetFolder(journal.transactionAssetPath + "/EmitterMaterials");
            AreaLitOcclusionPaths.EnsureAssetFolder(journal.transactionAssetPath + "/ProxyMeshes");
            AreaLitOcclusionPaths.EnsureAssetFolder(journal.bakeOutputAssetPath);
            AreaLitOcclusionPaths.EnsureAssetFolder(journal.generatedOutputAssetPath);

            var loadedScenes = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) loadedScenes.Add(scene);
            }

            for (var index = 0; index < loadedScenes.Count; index++)
            {
                var sourceScene = loadedScenes[index];
                var copyFolder = journal.transactionAssetPath + "/Scenes/" + index;
                AreaLitOcclusionPaths.EnsureAssetFolder(copyFolder);
                var stagingPath = copyFolder + "/" + Path.GetFileName(sourceScene.path);

                journal.sceneCopies.Add(new JournalSceneCopy
                {
                    sourcePath = sourceScene.path,
                    stagingPath = stagingPath
                });
                AreaLitOcclusionJournalStore.Save(journal);

                if (!EditorSceneManager.SaveScene(sourceScene, stagingPath, true))
                {
                    throw new IOException("Unity failed to create staging copy: " + stagingPath);
                }
            }

            journal.state = "StagingScenesCreated";
            AreaLitOcclusionJournalStore.Save(journal);
            AssetDatabase.Refresh();
        }

        private static void OpenAndMutateStagingScenes(AreaLitOcclusionJournal journal)
        {
            for (var index = 0; index < journal.sceneCopies.Count; index++)
            {
                var mode = index == 0 ? OpenSceneMode.Single : OpenSceneMode.Additive;
                EditorSceneManager.OpenScene(journal.sceneCopies[index].stagingPath, mode);
            }

            var activeCopy = journal.sceneCopies.First(copy =>
                string.Equals(copy.sourcePath, journal.activeSourceScenePath, StringComparison.OrdinalIgnoreCase));
            var activeStagingScene = SceneManager.GetSceneByPath(activeCopy.stagingPath);
            EditorSceneManager.SetActiveScene(activeStagingScene);

            DisableAllStagingLights();

            var proxyRoots = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            for (var emitterIndex = 0; emitterIndex < journal.emitters.Count; emitterIndex++)
            {
                var emitter = journal.emitters[emitterIndex];
                var copy = journal.sceneCopies.FirstOrDefault(item =>
                    string.Equals(item.sourcePath, emitter.rendererLocator.sourceScenePath, StringComparison.OrdinalIgnoreCase));
                if (copy == null)
                {
                    throw new InvalidOperationException("No staging scene exists for AreaLit emitter " + emitter.rendererLocator.siblingPath);
                }

                var stagingScene = SceneManager.GetSceneByPath(copy.stagingPath);
                var renderer = AreaLitOcclusionDiscovery.ResolveLocator(stagingScene, emitter.rendererLocator) as Renderer;
                if (renderer == null)
                {
                    throw new InvalidOperationException("An AreaLit emitter could not be resolved in the staging scene. No bake was started.");
                }

                ConfigureStagingEmitterMaterial(journal, renderer, emitter, emitterIndex);
                if (!emitter.selected) continue;

                Transform proxyRoot;
                if (!proxyRoots.TryGetValue(stagingScene.path, out proxyRoot))
                {
                    var rootObject = new GameObject("__AreaLit Occlusion Proxies");
                    SceneManager.MoveGameObjectToScene(rootObject, stagingScene);
                    proxyRoot = rootObject.transform;
                    proxyRoots.Add(stagingScene.path, proxyRoot);
                }

                CreateStagingProxy(journal, renderer, emitter, emitterIndex, proxyRoot);
            }

            if (!EditorSceneManager.SaveOpenScenes())
            {
                throw new IOException("Unity could not save the isolated staging scenes after configuring their lights.");
            }

            journal.state = "StagingConfigured";
            AreaLitOcclusionJournalStore.Save(journal);
            Notify("Staging scenes configured: normal lights are off and selected AreaLit emitters own the Bakery proxies.");
        }

        private static void DisableAllStagingLights()
        {
            DisableComponents(AreaLitOcclusionBakery.FindBakeryLights(), false);
            DisableComponents(AreaLitOcclusionBakery.FindBakeryLightMeshes(), true);

            foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>(true))
            {
                if (!IsOpenSceneObject(light)) continue;
                light.enabled = false;
                EditorUtility.SetDirty(light);
            }
        }

        private static void DisableComponents(IEnumerable<Component> components, bool disableRenderer)
        {
            foreach (var component in components)
            {
                if (!IsOpenSceneObject(component)) continue;
                var serialized = new SerializedObject(component);
                var enabledProperty = serialized.FindProperty("m_Enabled");
                if (enabledProperty != null) enabledProperty.boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(component);

                if (!disableRenderer) continue;
                var renderer = component.GetComponent<Renderer>();
                if (renderer == null) continue;
                renderer.enabled = false;
                EditorUtility.SetDirty(renderer);
            }
        }

        private static bool IsOpenSceneObject(Component component)
        {
            return component != null && component.gameObject != null &&
                   component.gameObject.scene.IsValid() && component.gameObject.scene.isLoaded;
        }

        private static void ConfigureStagingEmitterMaterial(
            AreaLitOcclusionJournal journal,
            Renderer renderer,
            JournalEmitter emitter,
            int emitterIndex)
        {
            var materials = renderer.sharedMaterials;
            if (emitter.materialSlot < 0 || emitter.materialSlot >= materials.Length || materials[emitter.materialSlot] == null)
            {
                throw new InvalidOperationException("An AreaLit emitter material slot changed before staging could be configured.");
            }

            var sourceMaterial = materials[emitter.materialSlot];
            var clone = new Material(sourceMaterial)
            {
                name = sourceMaterial.name + " (Occlusion " + emitter.channel + ")"
            };
            var clonePath = journal.transactionAssetPath + "/EmitterMaterials/" +
                            emitterIndex.ToString("D3") + "_" +
                            AreaLitOcclusionPaths.SanitizeFileName(sourceMaterial.name) + ".mat";

            if (emitter.selected)
            {
                var brightness = GetEffectiveBakeIntensity(journal, emitter);
                var sourceColor = sourceMaterial.GetColor(EmitterColorProperty);
                var channelColor = GetChannelColor(emitter.channel);
                channelColor.r *= brightness;
                channelColor.g *= brightness;
                channelColor.b *= brightness;
                channelColor.a = sourceColor.a;
                clone.SetColor(EmitterColorProperty, channelColor);
                clone.SetFloat(EmitterIntensityProperty, 1f);
                clone.SetFloat(EmitterChannelProperty, (float)emitter.channel);
            }
            else
            {
                var sourceColor = sourceMaterial.GetColor(EmitterColorProperty);
                clone.SetColor(EmitterColorProperty, new Color(0f, 0f, 0f, sourceColor.a));
                clone.SetFloat(EmitterIntensityProperty, 0f);
            }

            AssetDatabase.CreateAsset(clone, clonePath);
            materials[emitter.materialSlot] = clone;
            renderer.sharedMaterials = materials;
            if (emitter.selected || emitter.rendererWasEnabled) renderer.enabled = true;
            EditorUtility.SetDirty(renderer);
        }

        private static void CreateStagingProxy(
            AreaLitOcclusionJournal journal,
            Renderer emitterRenderer,
            JournalEmitter emitter,
            int emitterIndex,
            Transform proxyRoot)
        {
            var geometryTransform = emitterRenderer.transform;
            var sourceFilter = emitterRenderer.GetComponent<MeshFilter>();
            if (sourceFilter == null || sourceFilter.sharedMesh == null || emitter.sourceSubmesh < 0)
            {
                throw new InvalidOperationException("AreaLit emitter proxy geometry is unavailable.");
            }
            var proxyMesh = CreateProxySubmeshAsset(journal, sourceFilter.sharedMesh, emitter.sourceSubmesh, emitterIndex);

            var proxyObject = new GameObject("AreaLit Occlusion Proxy - " + emitterRenderer.name + " [" + emitter.channel + "]");
            SceneManager.MoveGameObjectToScene(proxyObject, emitterRenderer.gameObject.scene);
            proxyObject.transform.SetParent(proxyRoot, false);
            proxyObject.transform.SetPositionAndRotation(geometryTransform.position, geometryTransform.rotation);
            proxyObject.transform.localScale = geometryTransform.lossyScale;
            if (!MatricesApproximately(proxyObject.transform.localToWorldMatrix, geometryTransform.localToWorldMatrix, 0.002f))
            {
                UnityEngine.Object.DestroyImmediate(proxyObject);
                throw new InvalidOperationException(
                    "AreaLit emitter '" + emitterRenderer.name + "' uses a sheared world transform that cannot be reproduced by an isolated proxy.");
            }

            var filter = proxyObject.AddComponent<MeshFilter>();
            filter.sharedMesh = proxyMesh;
            var meshRenderer = proxyObject.AddComponent<MeshRenderer>();
            var bakeryRuntimePath = AreaLitOcclusionBakery.GetRuntimePath();
            var proxyMaterial = AssetDatabase.LoadAssetAtPath<Material>(bakeryRuntimePath + "ftDefaultAreaLightMat.mat");
            if (proxyMaterial == null)
            {
                throw new InvalidOperationException("Bakery's default area-light material could not be loaded.");
            }
            meshRenderer.sharedMaterial = proxyMaterial;

            var proxy = AreaLitOcclusionBakery.AddLightMeshProxy(
                proxyObject,
                GetChannelColor(emitter.channel),
                GetEffectiveBakeIntensity(journal, emitter));
            meshRenderer.enabled = true;
            GameObjectUtility.SetStaticEditorFlags(proxyObject, StaticEditorFlags.ContributeGI);
            EditorUtility.SetDirty(proxy);
            EditorUtility.SetDirty(meshRenderer);
        }

        private static float GetEffectiveBakeIntensity(AreaLitOcclusionJournal journal, JournalEmitter emitter)
        {
            var multiplier = journal != null && journal.bakeIntensityMultiplierCaptured
                ? AreaLitOcclusionDiscovery.NormalizeBakeIntensity(journal.bakeIntensityMultiplier, 1f)
                : 1f;
            return AreaLitOcclusionDiscovery.NormalizeBakeIntensity(
                emitter.bakeIntensity * multiplier,
                emitter.bakeIntensity);
        }

        private static Mesh CreateProxySubmeshAsset(
            AreaLitOcclusionJournal journal,
            Mesh sourceMesh,
            int submesh,
            int emitterIndex)
        {
            if (submesh < 0 || submesh >= sourceMesh.subMeshCount)
            {
                throw new InvalidOperationException("AreaLit emitter submesh is outside the source mesh.");
            }

            var proxyMesh = new Mesh { name = sourceMesh.name + " (AreaLit Occlusion Proxy)" };
            var writableApplied = false;
            using (var sourceDataArray = MeshUtility.AcquireReadOnlyMeshData(sourceMesh))
            {
                var writableDataArray = Mesh.AllocateWritableMeshData(1);
                try
                {
                    var sourceData = sourceDataArray[0];
                    var destinationData = writableDataArray[0];
                    destinationData.SetVertexBufferParams(sourceData.vertexCount, sourceMesh.GetVertexAttributes());
                    for (var stream = 0; stream < sourceData.vertexBufferCount; stream++)
                    {
                        var sourceVertices = sourceData.GetVertexData<byte>(stream);
                        var destinationVertices = destinationData.GetVertexData<byte>(stream);
                        if (sourceVertices.Length != destinationVertices.Length)
                        {
                            throw new InvalidOperationException("Unity returned an unexpected vertex buffer size while copying AreaLit geometry.");
                        }
                        NativeArray<byte>.Copy(sourceVertices, destinationVertices);
                    }

                    var sourceIndices = sourceData.GetIndexData<byte>();
                    var indexStride = sourceData.indexFormat == IndexFormat.UInt16 ? sizeof(ushort) : sizeof(uint);
                    if (sourceIndices.Length % indexStride != 0)
                    {
                        throw new InvalidOperationException("Unity returned an invalid AreaLit index buffer size.");
                    }
                    destinationData.SetIndexBufferParams(sourceIndices.Length / indexStride, sourceData.indexFormat);
                    var destinationIndices = destinationData.GetIndexData<byte>();
                    NativeArray<byte>.Copy(sourceIndices, destinationIndices);

                    destinationData.subMeshCount = 1;
                    destinationData.SetSubMesh(
                        0,
                        sourceData.GetSubMesh(submesh),
                        MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                    Mesh.ApplyAndDisposeWritableMeshData(
                        writableDataArray,
                        proxyMesh,
                        MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                    writableApplied = true;
                }
                finally
                {
                    if (!writableApplied) writableDataArray.Dispose();
                }
            }
            proxyMesh.bounds = sourceMesh.bounds;

            var meshPath = journal.transactionAssetPath + "/ProxyMeshes/" +
                           emitterIndex.ToString("D3") + "_" +
                           AreaLitOcclusionPaths.SanitizeFileName(sourceMesh.name) + ".asset";
            try
            {
                AssetDatabase.CreateAsset(proxyMesh, meshPath);
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(proxyMesh);
                throw;
            }
            return proxyMesh;
        }

        private static bool MatricesApproximately(Matrix4x4 left, Matrix4x4 right, float tolerance)
        {
            for (var index = 0; index < 16; index++)
            {
                if (Mathf.Abs(left[index] - right[index]) > tolerance) return false;
            }
            return true;
        }

        private static Color GetChannelColor(OcclusionChannel channel)
        {
            switch (channel)
            {
                case OcclusionChannel.Red: return new Color(1f, 0f, 0f, 1f);
                case OcclusionChannel.Green: return new Color(0f, 1f, 0f, 1f);
                case OcclusionChannel.Blue: return new Color(0f, 0f, 1f, 1f);
                default: return new Color(0f, 0f, 0f, 1f);
            }
        }

        private static object ConfigureBakeryForTransaction(AreaLitOcclusionJournal journal)
        {
            // Opening the Bakery window loads scene-specific render settings and may rewrite its
            // static output path. Do that before capturing and replacing the values below.
            var bakeryWindow = AreaLitOcclusionBakery.GetOrOpenRenderWindow();
            AreaLitOcclusionBakery.LoadRenderSettings(bakeryWindow);

            AreaLitOcclusionBakery.OutputPath = journal.bakeOutputAssetPath.Substring("Assets/".Length);
            AreaLitOcclusionBakery.UseScenePath = false;
            var settings = AreaLitOcclusionBakery.GetProjectSettings();
            if (settings != null)
            {
                // Staging scenes still reference the user's normal lightmap assets. Keep Bakery's
                // deletion option off for the entire inspection session, including a bake started
                // directly from Bakery, then restore the user's value when the transaction ends.
                AreaLitOcclusionBakery.SetDeletePreviousLightmaps(settings, false);
            }
            return bakeryWindow;
        }

        private static void StartBakery(AreaLitOcclusionJournal journal)
        {
            journal.state = "StartingBake";
            AreaLitOcclusionJournalStore.Save(journal);

            var bakeryWindow = ConfigureBakeryForTransaction(journal);

            var settings = AreaLitOcclusionBakery.GetProjectSettings();
            if (AreaLitOcclusionBakery.GetDeletePreviousLightmaps(settings))
            {
                // A copied scene still references the user's existing lightmap assets. Allowing
                // Bakery's deletion option here could delete those shared assets, so it is disabled
                // only for the synchronous RenderButton setup and never saved to the project asset.
                AreaLitOcclusionBakery.SetDeletePreviousLightmaps(settings, false);
            }

            completionReceived = false;
            bakeWasObserved = false;
            SubscribeToBakery();

            journal.state = "Baking";
            AreaLitOcclusionJournalStore.Save(journal);
            try
            {
                AreaLitOcclusionBakery.StartRender(bakeryWindow);
            }
            finally
            {
                // RenderButton reads this option synchronously before starting its coroutine. Put
                // the user's in-memory project setting back immediately; it is never saved by us.
                if (settings != null)
                {
                    AreaLitOcclusionBakery.SetDeletePreviousLightmaps(
                        settings,
                        journal.previousDeletePreviousLightmaps);
                }
            }

            if (!AreaLitOcclusionBakery.BakeInProgress)
            {
                throw new InvalidOperationException("Bakery did not start. Check the Console and Bakery configuration for a validation message.");
            }

            bakeWasObserved = true;
            Notify("Bakery is rendering the isolated occlusion bake...");
        }

        private static void SubscribeToBakery()
        {
            if (monitoring) return;
            AreaLitOcclusionBakery.SubscribeFinished(OnBakeryFinished);
            EditorApplication.update += MonitorBakery;
            monitoring = true;
        }

        private static void UnsubscribeFromBakery()
        {
            if (!monitoring) return;
            AreaLitOcclusionBakery.UnsubscribeFinished();
            EditorApplication.update -= MonitorBakery;
            monitoring = false;
        }

        private static void MonitorBakery()
        {
            if (AreaLitOcclusionBakery.BakeInProgress)
            {
                if (IsPreparedForInspection)
                {
                    activeJournal.state = "Baking";
                    AreaLitOcclusionJournalStore.Save(activeJournal);
                    Notify("Bakery render detected; the prepared occlusion transaction is now being tracked.");
                }
                bakeWasObserved = true;
                return;
            }

            if (!bakeWasObserved || completionReceived) return;

            UnsubscribeFromBakery();
            EditorApplication.delayCall += HandleBakeStoppedWithoutCompletion;
        }

        private static void HandleBakeStoppedWithoutCompletion()
        {
            if (activeJournal == null || completionReceived) return;

            var reason = AreaLitOcclusionBakery.UserCanceled
                ? "The Bakery bake was canceled."
                : "Bakery stopped without reporting a completed full render.";
            CancelAndRestore(activeJournal, reason);
        }

        private static void OnBakeryFinished(object sender, EventArgs args)
        {
            completionReceived = true;
            UnsubscribeFromBakery();
            EditorApplication.delayCall += FinalizeSuccessfulBake;
        }

        private static void FinalizeSuccessfulBake()
        {
            var journal = activeJournal ?? AreaLitOcclusionJournalStore.LoadActive();
            if (journal == null) return;

            try
            {
                journal.state = "CollectingOutputs";
                AreaLitOcclusionJournalStore.Save(journal);
                var plan = CollectBakeOutputPlan(journal);

                // Return to the original scenes before touching shared material assets.
                AreaLitOcclusionRecovery.RestoreBakerySettings(journal);
                AreaLitOcclusionRecovery.RestoreOriginalSceneSetup(journal);

                journal.state = "Publishing";
                AreaLitOcclusionJournalStore.Save(journal);
                var sourceToPublished = PublishTextures(journal, plan.sourceTexturePaths);

                var appliedCount = ApplyMaterials(journal, plan, sourceToPublished);

                journal.state = "Completed";
                journal.error = string.Join("\n", plan.warnings.ToArray());
                AreaLitOcclusionJournalStore.Archive(journal);
                activeJournal = null;

                Notify("Occlusion bake completed: " + sourceToPublished.Count + " stable texture(s), " + appliedCount + " receiver material(s) configured." +
                    (plan.warnings.Count == 0 ? "" : " " + plan.warnings.Count + " receiver warning(s); see Console."));
                Debug.Log("[AreaLit Occlusion] " + LastStatus +
                          (plan.warnings.Count == 0 ? string.Empty : "\nWarnings:\n" + string.Join("\n", plan.warnings.ToArray())));
            }
            catch (Exception exception)
            {
                FailAndRecover(journal, "The bake finished, but safe publication failed and was rolled back.", exception);
            }
        }

        internal static BakeOutputPlan CollectBakeOutputPlan(AreaLitOcclusionJournal journal)
        {
            var plan = new BakeOutputPlan();
            var outputPrefix = journal.bakeOutputAssetPath.TrimEnd('/') + "/";
            var lightmaps = LightmapSettings.lightmaps;

            foreach (var lightmap in lightmaps)
            {
                if (lightmap == null || lightmap.lightmapColor == null) continue;
                var path = AssetDatabase.GetAssetPath(lightmap.lightmapColor);
                if (!string.IsNullOrEmpty(path) && path.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    plan.sourceTexturePaths.Add(path);
                }
            }

            if (plan.sourceTexturePaths.Count == 0)
            {
                throw new InvalidOperationException("Bakery completed but no color lightmaps were found in the isolated transaction output folder.");
            }

            if (journal.receiverMaterialPaths.Count == 0) return plan;

            var selectedMaterials = new HashSet<string>(journal.receiverMaterialPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!renderer.gameObject.scene.IsValid() || !renderer.gameObject.scene.isLoaded) continue;
                var sceneCopy = journal.sceneCopies.FirstOrDefault(copy => copy.stagingPath == renderer.gameObject.scene.path);
                if (sceneCopy == null) continue;
                var lightmapIndex = renderer.lightmapIndex;
                if (lightmapIndex < 0 || lightmapIndex >= lightmaps.Length) continue;
                var lightmap = lightmaps[lightmapIndex];
                if (lightmap == null || lightmap.lightmapColor == null) continue;
                var sourcePath = AssetDatabase.GetAssetPath(lightmap.lightmapColor);
                if (!plan.sourceTexturePaths.Contains(sourcePath)) continue;

                var slots = renderer.sharedMaterials;
                for (var slot = 0; slot < slots.Length; slot++)
                {
                    var material = slots[slot];
                    if (material == null) continue;
                    var materialPath = AssetDatabase.GetAssetPath(material);
                    if (!selectedMaterials.Contains(materialPath)) continue;

                    var locator = AreaLitOcclusionDiscovery.CreateLocator(renderer);
                    locator.sourceScenePath = sceneCopy.sourcePath;
                    plan.receivers.Add(new BakedReceiver { locator = locator, slot = slot,
                        materialPath = materialPath, sourcePath = sourcePath, scaleOffset = renderer.lightmapScaleOffset });
                }
            }

            foreach (var materialPath in selectedMaterials)
            {
                if (!plan.receivers.Any(receiver => receiver.materialPath == materialPath))
                {
                    plan.warnings.Add("No generated lightmap was found for selected receiver material: " + materialPath);
                }
            }

            return plan;
        }

        private static Dictionary<string, string> PublishTextures(AreaLitOcclusionJournal journal, HashSet<string> sourcePaths)
        {
            AreaLitOcclusionPaths.EnsureAssetFolder(journal.generatedOutputAssetPath);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var singleOutput = sourcePaths.Count == 1;
            var brightness = journal.outputAdjustmentsCaptured ? journal.outputBrightness : 1f;
            var contrast = journal.outputAdjustmentsCaptured ? journal.outputContrast : 1f;

            foreach (var sourcePath in sourcePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
                var destinationName = singleOutput
                    ? "occlusion_map" + extension
                    : "occlusion_" + AreaLitOcclusionPaths.SanitizeFileName(sourceName) + extension;
                var destinationPath = journal.generatedOutputAssetPath + "/" + destinationName;

                BackUpAssetBeforeWrite(journal, destinationPath, false);
                var destinationExists = File.Exists(AreaLitOcclusionPaths.ToAbsolutePath(destinationPath));
                // Neutral sliders still need to clamp HDR bake values to valid visibility.
                AreaLitOcclusionImageAdjuster.WriteAdjustedTexture(
                    sourcePath,
                    destinationPath,
                    brightness,
                    contrast);
                AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceUpdate);

                if (!destinationExists)
                {
                    ConfigureNewOcclusionImporter(sourcePath, destinationPath);
                }

                journal.publishedOutputPaths.Add(destinationPath);
                AreaLitOcclusionJournalStore.Save(journal);
                result.Add(sourcePath, destinationPath);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            return result;
        }

        private static void ConfigureNewOcclusionImporter(string sourcePath, string destinationPath)
        {
            var importer = AssetImporter.GetAtPath(destinationPath) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("Published occlusion texture has no TextureImporter: " + destinationPath);
            }

            // A Bakery color lightmap is imported as a Unity Lightmap by default. AreaLit expects a
            // regular material texture. These settings match the project's existing replacement-file
            // workflow while retaining the copied source's per-platform size/compression overrides.
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Bilinear;
            importer.alphaSource = TextureImporterAlphaSource.None;

            var sourceTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath);
            if (sourceTexture != null)
            {
                importer.maxTextureSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(sourceTexture.width, sourceTexture.height)), 32, 8192);
            }
            importer.SaveAndReimport();
        }

        internal static int ApplyMaterials(
            AreaLitOcclusionJournal journal, BakeOutputPlan plan,
            Dictionary<string, string> sourceToPublished)
        {
            var updates = new List<(Material material, string path, Vector4 uv, bool applyUv, Texture2D texture)>();
            var loadedRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            foreach (var entries in plan.receivers.GroupBy(item => item.materialPath))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(entries.Key);
                if (material == null || !material.HasProperty(ReceiverTextureProperty) ||
                    !entries.Key.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    !AssetDatabase.IsOpenForEdit(material) || EditorUtility.IsDirty(material))
                {
                    plan.warnings.Add("Receiver is missing, read-only, or has unsaved edits; no settings changed: " + entries.Key);
                    continue;
                }

                var first = entries.First();
                var applyUv = true;
                var textures = new HashSet<Texture2D>();
                var bakedUsers = new HashSet<Renderer>();
                foreach (var entry in entries)
                {
                    var scene = SceneManager.GetSceneByPath(entry.locator.sourceScenePath);
                    var renderer = AreaLitOcclusionDiscovery.ResolveLocator(scene, entry.locator) as MeshRenderer;
                    if (renderer == null || entry.slot < 0 || entry.slot >= renderer.sharedMaterials.Length ||
                        renderer.sharedMaterials[entry.slot] != material)
                        throw new InvalidOperationException("Baked receiver assignment changed: " + entries.Key);
                    for (var component = 0; component < 4; component++)
                        if (float.IsNaN(entry.scaleOffset[component]) || float.IsInfinity(entry.scaleOffset[component]))
                            throw new InvalidOperationException("Baked receiver has invalid atlas coordinates: " + entries.Key);
                    bakedUsers.Add(renderer);
                    applyUv &= AreaLitOcclusionUvTools.Approximately(first.scaleOffset, entry.scaleOffset);
                    if (journal.applyToMaterials)
                    {
                        string publishedPath;
                        Texture2D texture;
                        if (!sourceToPublished.TryGetValue(entry.sourcePath, out publishedPath) ||
                            (texture = AssetDatabase.LoadAssetAtPath<Texture2D>(publishedPath)) == null)
                            throw new InvalidOperationException("Baked receiver atlas was not published: " + entry.sourcePath);
                        textures.Add(texture);
                    }
                }

                // Never resolve shared-material conflicts by cloning or reassigning materials.
                if (loadedRenderers.Any(r => !bakedUsers.Contains(r) && r.sharedMaterials.Contains(material)))
                {
                    plan.warnings.Add("Shared-material conflict: a loaded user has no baked mapping; no settings changed: " + entries.Key);
                    continue;
                }
                if (!applyUv)
                    plan.warnings.Add("Shared-material conflict: different atlas tiling/offsets; UV settings left unchanged: " + entries.Key);
                if (textures.Count > 1)
                    plan.warnings.Add("Shared-material conflict: multiple baked atlases; occlusion texture left unchanged: " + entries.Key);
                var assignedTexture = textures.Count == 1 ? textures.First() : null;
                if (applyUv || assignedTexture != null)
                    updates.Add((material, entries.Key, first.scaleOffset, applyUv, assignedTexture));
            }

            foreach (var item in updates)
            {
                BackUpAssetBeforeWrite(journal, item.path, true);
                Undo.RecordObject(item.material, "Apply AreaLit occlusion settings");
                if (item.texture != null) item.material.SetTexture(ReceiverTextureProperty, item.texture);
                if (item.applyUv) AreaLitOcclusionUvTools.ApplyScaleOffset(item.material, item.uv, true);
                EditorUtility.SetDirty(item.material);
                AssetDatabase.SaveAssetIfDirty(item.material);
            }
            Undo.FlushUndoRecordObjects();
            return updates.Count;
        }
        private static void BackUpAssetBeforeWrite(AreaLitOcclusionJournal journal, string assetPath, bool material)
        {
            var list = material ? journal.materialBackups : journal.outputBackups;
            if (list.Any(backup => string.Equals(backup.assetPath, assetPath, StringComparison.OrdinalIgnoreCase))) return;

            var absolutePath = AreaLitOcclusionPaths.ToAbsolutePath(assetPath);
            var existed = File.Exists(absolutePath);
            var backupFolder = Path.Combine(
                AreaLitOcclusionPaths.ToAbsolutePath(journal.transactionAssetPath),
                "Backups",
                material ? "Materials" : "Outputs");
            Directory.CreateDirectory(backupFolder);

            var backupPath = Path.Combine(backupFolder, Guid.NewGuid().ToString("N") + ".backup");
            if (existed) File.Copy(absolutePath, backupPath, false);

            list.Add(new JournalFileBackup
            {
                assetPath = assetPath,
                backupAbsolutePath = backupPath,
                existed = existed
            });
            // The journal is committed before the destination is changed.
            AreaLitOcclusionJournalStore.Save(journal);
        }

        private static void CancelAndRestore(AreaLitOcclusionJournal journal, string reason)
        {
            UnsubscribeFromBakery();
            try
            {
                AreaLitOcclusionRecovery.RestoreBakerySettings(journal);
                AreaLitOcclusionRecovery.RestoreOriginalSceneSetup(journal);
                journal.state = "Canceled";
                journal.error = reason;
                AreaLitOcclusionJournalStore.Archive(journal);
                activeJournal = null;
                Notify(reason + " Original scenes were restored; temporary transaction data is queued for cleanup.");
            }
            catch (Exception exception)
            {
                journal.state = "RecoveryRequired";
                journal.error = reason + "\n" + exception;
                AreaLitOcclusionJournalStore.Save(journal);
                activeJournal = null;
                Notify(reason + " Automatic scene restoration needs attention; the recovery journal and staging data were retained.");
                AreaLitOcclusionRecovery.ScheduleRecoveryCheck();
            }
        }

        private static void FailAndRecover(AreaLitOcclusionJournal journal, string context, Exception exception)
        {
            UnsubscribeFromBakery();
            journal.error = context + "\n" + exception;
            AreaLitOcclusionJournalStore.Save(journal);
            var recovered = AreaLitOcclusionRecovery.TryRecover(journal, false);
            activeJournal = null;
            Notify(context + (recovered ? " Recovery completed." : " Recovery data was retained for manual restoration."));
            Debug.LogError("[AreaLit Occlusion] " + context + "\n" + exception);
        }

        private static void Notify(string status)
        {
            LastStatus = status;
            var handler = StateChanged;
            if (handler != null) handler();
        }
    }
}
