using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.AreaLitOcclusion
{
    internal sealed class AreaLitOcclusionWindow : EditorWindow
    {
        private const float BodyHorizontalPadding = 12f;
        private const float ItemHorizontalPadding = 8f;
        private const float ItemDetailIndent = ItemHorizontalPadding + 26f;

        private DiscoveryResult discovery;
        private Vector2 scroll;
        private bool showEmitters = true;
        private bool showReceivers;
        private bool showDebug = true;
        private bool applyToMaterials = true;
        [SerializeField] private bool showDisabledReceivers;
        [SerializeField] private float bakeIntensityMultiplier = 1f;
        [SerializeField] private float outputBrightness = 1f;
        [SerializeField] private float outputContrast = 1f;
        [SerializeField] private Texture outputPreviewOverride;
        private string outputAdjustmentStatus;
        private Material outputAdjustmentPreviewMaterial;
        private string outputAdjustmentPreviewError;

        [MenuItem("Tools/Lightbulb/AreaLit Occlusion Baker")]
        public static void OpenWindow()
        {
            var window = GetWindow<AreaLitOcclusionWindow>();
            window.titleContent = new GUIContent("AreaLit Occlusion");
            window.minSize = new Vector2(520f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            AreaLitOcclusionBakeController.StateChanged += OnControllerStateChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            EditorApplication.projectChanged += OnProjectChanged;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            RefreshDiscovery();
            AreaLitOcclusionRecovery.ScheduleRecoveryCheck();
        }

        private void OnDisable()
        {
            AreaLitOcclusionBakeController.StateChanged -= OnControllerStateChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.projectChanged -= OnProjectChanged;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            if (outputAdjustmentPreviewMaterial != null)
            {
                DestroyImmediate(outputAdjustmentPreviewMaterial);
                outputAdjustmentPreviewMaterial = null;
            }
        }

        private void OnControllerStateChanged()
        {
            if (!AreaLitOcclusionBakeController.IsRunning &&
                !AreaLitOcclusionBakeController.IsPreparedForInspection)
            {
                RefreshDiscovery();
            }
            else
            {
                Repaint();
            }
        }

        private void OnHierarchyChanged()
        {
            if (!AreaLitOcclusionBakeController.IsRunning) RefreshDiscovery();
        }

        private void OnUndoRedoPerformed()
        {
            if (!AreaLitOcclusionBakeController.IsRunning) RefreshDiscovery();
        }

        private void OnProjectChanged()
        {
            if (!AreaLitOcclusionBakeController.IsRunning) RefreshDiscovery();
        }

        private void RefreshDiscovery()
        {
            discovery = AreaLitOcclusionDiscovery.ScanLoadedScenes();
            Repaint();
        }

        private void OnGUI()
        {
            DrawHeader();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(BodyHorizontalPadding);
                using (new EditorGUILayout.VerticalScope())
                {
                    DrawRecovery();
                    if (AreaLitOcclusionBakeController.IsPreparedForInspection)
                    {
                        DrawPreparedInspection();
                    }
                    else
                    {
                        DrawSummary();
                        DrawEmitters();
                        DrawReceivers();
                        DrawOutputAdjustments();
                        DrawDebugTools();
                        DrawOutput();
                    }
                }
                GUILayout.Space(BodyHorizontalPadding);
            }
            EditorGUILayout.EndScrollView();

            if (!AreaLitOcclusionBakeController.IsPreparedForInspection)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(BodyHorizontalPadding);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        DrawBakeButton();
                    }
                    GUILayout.Space(BodyHorizontalPadding);
                }
            }
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("AreaLit Occlusion", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(AreaLitOcclusionBakeController.IsRunning))
                {
                    if (GUILayout.Button("Rescan", EditorStyles.toolbarButton)) RefreshDiscovery();
                    if (GUILayout.Button("Configuration Audit", EditorStyles.toolbarButton)) AreaLitAuditWindow.OpenWindow();
                }
            }

            if (!string.IsNullOrEmpty(AreaLitOcclusionBakeController.LastStatus))
            {
                EditorGUILayout.HelpBox(AreaLitOcclusionBakeController.LastStatus, MessageType.Info);
            }
            if (!AreaLitOcclusionBakery.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    AreaLitOcclusionBakery.UnavailableReason +
                    " Output adjustment remains available. Material setup and UV tools are in AreaLit Configuration Audit.",
                    MessageType.Warning);
            }
        }

        private void DrawRecovery()
        {
            if (!AreaLitOcclusionJournalStore.HasActiveJournal) return;

            var journal = AreaLitOcclusionJournalStore.LoadActive();
            var state = journal == null ? "Unreadable journal" : journal.state;

            if (journal != null && AreaLitOcclusionBakeController.IsPreparedForInspection)
            {
                EditorGUILayout.HelpBox(
                    "Inspection mode is active. The Hierarchy and Scene view contain isolated staging copies; your original scenes and light components are untouched.",
                    MessageType.Info);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Bake Prepared Scene", GUILayout.Height(28f)))
                    {
                        try
                        {
                            AreaLitOcclusionBakeController.StartPreparedBake();
                        }
                        catch (ExitGUIException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            EditorUtility.DisplayDialog("AreaLit Occlusion", exception.Message, "OK");
                            return;
                        }
                        GUIUtility.ExitGUI();
                    }

                    if (GUILayout.Button("Revert to Original Scenes", GUILayout.Height(28f)))
                    {
                        try
                        {
                            AreaLitOcclusionBakeController.RevertPreparedScene();
                            RefreshDiscovery();
                        }
                        catch (ExitGUIException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            EditorUtility.DisplayDialog("AreaLit Occlusion", exception.Message, "OK");
                            return;
                        }
                        GUIUtility.ExitGUI();
                    }
                }
                return;
            }

            EditorGUILayout.HelpBox(
                "A protected occlusion transaction is active (" + state + "). Its staging scenes and recovery journal are being retained.",
                AreaLitOcclusionBakeController.IsRunning ? MessageType.Info : MessageType.Warning);

            if (!AreaLitOcclusionBakeController.IsRunning && GUILayout.Button("Restore Original Scenes and Roll Back Incomplete Changes"))
            {
                AreaLitOcclusionRecovery.TryRecoverActive(true);
                RefreshDiscovery();
            }
        }

        private void DrawPreparedInspection()
        {
            var journal = AreaLitOcclusionJournalStore.LoadActive();
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Inspect the prepared bake", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Normal Unity and Bakery lights are disabled. Selected AreaLit emitters use cloned channel-colored materials, " +
                "and their transaction-owned Bakery proxies are under __AreaLit Occlusion Proxies. " +
                "Inspect everything normally in the Hierarchy and Inspector. " +
                "Any scene edits you make here affect only transaction-owned copies. Start the render from this window or Bakery; both are tracked. " +
                "Use Revert to Original Scenes when you are done.",
                MessageType.None);

            if (journal == null) return;
            foreach (var copy in journal.sceneCopies)
            {
                EditorGUILayout.SelectableLabel(
                    copy.stagingPath,
                    EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        private void DrawSummary()
        {
            if (discovery == null)
            {
                EditorGUILayout.HelpBox("No scan result is available.", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Ready to configure", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                DrawCount(discovery.emitters.Count(emitter => emitter.selected), "selected emitters");
                DrawCount(discovery.emitters.Count(emitter => emitter.selected && emitter.intensityOverridden), "intensity overrides");
                DrawCount(discovery.receivers.Count(receiver => receiver.selected), "receiver materials");
            }

            foreach (var warning in discovery.warnings.Distinct())
            {
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            }
        }

        private static void DrawCount(int count, string label)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(95f)))
            {
                var numberStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
                var labelStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
                GUILayout.Label(count.ToString(), numberStyle);
                GUILayout.Label(label, labelStyle);
            }
        }

        private void DrawEmitters()
        {
            if (discovery == null) return;
            EditorGUILayout.Space(10f);
            showEmitters = EditorGUILayout.Foldout(showEmitters, "AreaLit emitters", true);
            if (!showEmitters) return;

            EditorGUILayout.HelpBox(
                "These AreaLit/LightMesh renderers are the only occlusion sources. Normal Unity and Bakery lights are disabled in staging. " +
                "Each checked emitter gets an isolated Bakery Light Mesh proxy built from its exact geometry and AreaLit material intensity, even when mesh Read/Write is disabled.",
                MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var nextMultiplier = EditorGUILayout.DelayedFloatField(
                    new GUIContent(
                        "Global intensity multiplier",
                        "Multiplies every selected emitter's bake intensity after its automatic value or manual override."),
                    bakeIntensityMultiplier);
                if (EditorGUI.EndChangeCheck())
                {
                    bakeIntensityMultiplier = AreaLitOcclusionDiscovery.NormalizeBakeIntensity(nextMultiplier, 1f);
                }

                using (new EditorGUI.DisabledScope(Mathf.Approximately(bakeIntensityMultiplier, 1f)))
                {
                    if (GUILayout.Button("Reset", GUILayout.Width(64f))) bakeIntensityMultiplier = 1f;
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("All"))
                {
                    foreach (var emitter in discovery.emitters)
                    {
                        SetEmitterSelected(emitter, emitter.canAutoProxy,
                            emitter.canAutoProxy ? "On · manually included" : "Off · proxy unavailable");
                    }
                }
                if (GUILayout.Button("None"))
                {
                    foreach (var emitter in discovery.emitters) SetEmitterSelected(emitter, false, "Off · manually excluded");
                }
                GUILayout.Space(12f);
                if (GUILayout.Button("Selected → R")) SetSelectedEmitterChannel(OcclusionChannel.Red);
                if (GUILayout.Button("Selected → G")) SetSelectedEmitterChannel(OcclusionChannel.Green);
                if (GUILayout.Button("Selected → B")) SetSelectedEmitterChannel(OcclusionChannel.Blue);
            }

            if (discovery.emitters.Count == 0)
            {
                EditorGUILayout.HelpBox("No loaded Renderer uses the AreaLit/LightMesh shader.", MessageType.Warning);
            }
            foreach (var emitter in discovery.emitters)
            {
                EditorGUILayout.Space(3f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(ItemHorizontalPadding);
                    bool nextSelected;
                    using (new EditorGUI.DisabledScope(!emitter.canAutoProxy))
                    {
                        nextSelected = DrawSelectionToggle(emitter.selected);
                    }
                    if (nextSelected != emitter.selected)
                    {
                        SetEmitterSelected(
                            emitter,
                            nextSelected,
                            nextSelected ? "On · manually included" : "Off · manually excluded");
                    }
                    EditorGUILayout.ObjectField(emitter.renderer, typeof(Renderer), true, GUILayout.Width(180f));
                    using (new EditorGUI.DisabledScope(!emitter.selected))
                    {
                        emitter.channel = (OcclusionChannel)EditorGUILayout.EnumPopup(emitter.channel, GUILayout.Width(65f));
                    }
                    DrawReason(emitter.selectionReason);
                }
                DrawItemDetail(emitter.hierarchyPath);
                DrawItemDetail(emitter.proxyStatus);
                DrawEmitterIntensity(emitter, bakeIntensityMultiplier);
                DrawItemSeparator();
            }
        }

        private static void DrawEmitterIntensity(EmitterCandidate emitter, float intensityMultiplier)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(ItemDetailIndent);
                EditorGUILayout.LabelField(
                    new GUIContent("Bake intensity", "HDR intensity used by the temporary Bakery Light Mesh proxy."),
                    EditorStyles.miniLabel,
                    GUILayout.Width(82f));
                using (new EditorGUI.DisabledScope(!emitter.selected))
                {
                    EditorGUI.BeginChangeCheck();
                    var nextIntensity = EditorGUILayout.DelayedFloatField(emitter.bakeIntensity, GUILayout.Width(72f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        emitter.bakeIntensity = AreaLitOcclusionDiscovery.NormalizeBakeIntensity(
                            nextIntensity,
                            emitter.automaticBakeIntensity);
                        emitter.intensityOverridden = true;
                    }

                    var normalizedMultiplier = AreaLitOcclusionDiscovery.NormalizeBakeIntensity(intensityMultiplier, 1f);
                    var modeLabel = emitter.intensityOverridden ? "Manual override" : "Auto · AreaLit material";
                    if (!Mathf.Approximately(normalizedMultiplier, 1f))
                    {
                        var effective = AreaLitOcclusionDiscovery.NormalizeBakeIntensity(
                            emitter.bakeIntensity * normalizedMultiplier,
                            emitter.bakeIntensity);
                        modeLabel += " · effective " + effective.ToString("0.###");
                    }
                    EditorGUILayout.LabelField(
                        modeLabel,
                        EditorStyles.miniLabel,
                        GUILayout.MinWidth(120f));
                    if (emitter.intensityOverridden && GUILayout.Button("Auto", EditorStyles.miniButton, GUILayout.Width(46f)))
                    {
                        emitter.bakeIntensity = emitter.automaticBakeIntensity;
                        emitter.intensityOverridden = false;
                    }
                }
            }
            EditorGUILayout.Space(3f);
        }

        private void SetEmitterSelected(EmitterCandidate emitter, bool selected, string reason)
        {
            if (selected && !emitter.canAutoProxy) return;
            emitter.selected = selected;
            emitter.selectionReason = reason;
        }

        private void SetSelectedEmitterChannel(OcclusionChannel channel)
        {
            foreach (var emitter in discovery.emitters.Where(emitter => emitter.selected)) emitter.channel = channel;
        }

        private void DrawReceivers()
        {
            if (discovery == null) return;
            EditorGUILayout.Space(10f);
            showReceivers = EditorGUILayout.Foldout(showReceivers, "Mochie/AreaLit receiver materials", true);
            if (!showReceivers) return;

            applyToMaterials = EditorGUILayout.ToggleLeft("Assign generated occlusion textures after baking", applyToMaterials);
            EditorGUILayout.HelpBox("Selected receivers get UV1 and the new bake's tiling/offset when the mapping is unambiguous. Shared-material conflicts are reported and conflicting settings are left unchanged; no material variants are created. Turn texture assignment off to keep manually assigned maps.", MessageType.Info);
            var nextShowDisabled = EditorGUILayout.ToggleLeft(
                "Show materials with AreaLit disabled",
                showDisabledReceivers);
            if (nextShowDisabled != showDisabledReceivers)
            {
                showDisabledReceivers = nextShowDisabled;
                if (!showDisabledReceivers)
                {
                    foreach (var receiver in discovery.receivers.Where(receiver => !receiver.areaLitEnabled))
                    {
                        receiver.selected = false;
                        receiver.selectionReason = "Off · AreaLit disabled";
                    }
                }
            }

            var visibleReceivers = discovery.receivers
                .Where(receiver => receiver.areaLitEnabled || showDisabledReceivers)
                .ToArray();

            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("All"))
                    {
                        foreach (var receiver in visibleReceivers)
                        {
                            receiver.selected = true;
                            receiver.selectionReason = "On · manually included";
                        }
                    }
                    if (GUILayout.Button("None"))
                    {
                        foreach (var receiver in visibleReceivers)
                        {
                            receiver.selected = false;
                            receiver.selectionReason = "Off · manually excluded";
                        }
                    }
                }

                if (visibleReceivers.Length == 0)
                {
                    EditorGUILayout.HelpBox("No AreaLit-enabled receiver materials were found.", MessageType.Info);
                }

                foreach (var receiver in visibleReceivers)
                {
                    EditorGUILayout.Space(3f);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(ItemHorizontalPadding);
                        var nextSelected = DrawSelectionToggle(receiver.selected);
                        if (nextSelected != receiver.selected)
                        {
                            receiver.selected = nextSelected;
                            receiver.selectionReason = nextSelected ? "On · manually included" : "Off · manually excluded";
                        }
                        EditorGUILayout.ObjectField(receiver.material, typeof(Material), false, GUILayout.Width(190f));
                        DrawReason(receiver.selectionReason);
                    }
                    DrawItemDetail(receiver.rendererCount + " renderer use(s) · " + receiver.assetPath);
                    DrawItemSeparator();
                }
            }
        }

        private static bool DrawSelectionToggle(bool value)
        {
            // Use a fixed, unindented control rectangle. EditorGUI indentation inside the old 18px
            // layout rectangle left only a few pixels able to receive mouse clicks.
            var rect = GUILayoutUtility.GetRect(26f, 20f, GUILayout.Width(26f), GUILayout.Height(20f));
            return EditorGUI.Toggle(rect, value);
        }

        private static void DrawItemDetail(string detail)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(ItemDetailIndent);
                EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
            }
            EditorGUILayout.Space(3f);
        }

        private static void DrawItemSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1f);
            rect.xMin += ItemHorizontalPadding;
            rect.xMax -= ItemHorizontalPadding;
            var color = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.12f)
                : new Color(0f, 0f, 0f, 0.14f);
            EditorGUI.DrawRect(rect, color);
            EditorGUILayout.Space(2f);
        }

        private static void DrawReason(string reason)
        {
            EditorGUILayout.LabelField(new GUIContent(reason, reason), EditorStyles.miniLabel, GUILayout.MinWidth(145f));
        }

        private void DrawOutputAdjustments()
        {
            EditorGUILayout.Space(12f);
            EditorGUILayout.LabelField("Output adjustments", EditorStyles.boldLabel);

            var automaticPreview = GetAutomaticOutputPreviewTexture();
            var preview = outputPreviewOverride != null ? outputPreviewOverride : automaticPreview;
            using (new EditorGUILayout.HorizontalScope())
            {
                var previewRect = GUILayoutUtility.GetRect(
                    150f,
                    100f,
                    GUILayout.Width(150f),
                    GUILayout.Height(100f));
                var background = EditorGUIUtility.isProSkin
                    ? new Color(0.08f, 0.08f, 0.08f, 1f)
                    : new Color(0.22f, 0.22f, 0.22f, 1f);
                EditorGUI.DrawRect(previewRect, background);

                if (preview != null)
                {
                    var material = GetOutputAdjustmentPreviewMaterial();
                    if (material != null)
                    {
                        AreaLitOcclusionImageAdjuster.ConfigureMaterial(material, outputBrightness, outputContrast);
                        EditorGUI.DrawPreviewTexture(previewRect, preview, material, ScaleMode.ScaleToFit);
                    }
                }
                else
                {
                    GUI.Label(previewRect, "No occlusion map yet", EditorStyles.centeredGreyMiniLabel);
                }

                GUILayout.Space(10f);
                using (new EditorGUILayout.VerticalScope())
                {
                    outputPreviewOverride = (Texture)EditorGUILayout.ObjectField(
                        "Preview override",
                        outputPreviewOverride,
                        typeof(Texture),
                        false);
                    EditorGUILayout.LabelField(
                        outputPreviewOverride != null
                            ? "Showing override: " + outputPreviewOverride.name
                            : automaticPreview != null
                                ? "Auto: " + automaticPreview.name
                                : "Auto: no map assigned to a checked receiver",
                        EditorStyles.miniLabel);

                    EditorGUI.BeginChangeCheck();
                    outputBrightness = EditorGUILayout.Slider("Brightness", outputBrightness, 0f, 5f);
                    outputContrast = EditorGUILayout.Slider("Contrast", outputContrast, 0f, 2f);
                    if (EditorGUI.EndChangeCheck()) Repaint();

                    EditorGUILayout.LabelField(
                        "Preview and saved RGB are clamped to 0–1, including at default settings.",
                        EditorStyles.miniLabel);

                    var neutral = AreaLitOcclusionImageAdjuster.IsNeutral(outputBrightness, outputContrast);
                    var saveBlockReason = AreaLitOcclusionImageAdjuster.GetInPlaceAdjustmentBlockReason(preview);
                    if (AreaLitOcclusionBakeController.IsRunning || AreaLitOcclusionJournalStore.HasActiveJournal)
                    {
                        saveBlockReason = "Finish or revert the active bake transaction first.";
                    }
                    else if (EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        saveBlockReason = "Exit Play Mode before saving an adjusted texture.";
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(neutral))
                        {
                            if (GUILayout.Button("Reset", GUILayout.Width(64f)))
                            {
                                outputBrightness = 1f;
                                outputContrast = 1f;
                                Repaint();
                            }
                        }

                        using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(saveBlockReason)))
                        {
                            if (GUILayout.Button(
                                    new GUIContent("Save Adjustments to Current Map", saveBlockReason),
                                    GUILayout.MinWidth(190f)))
                            {
                                SaveOutputAdjustments(preview);
                            }
                        }
                    }

                    ManualAdjustmentRecord restorableAdjustment;
                    if (AreaLitOcclusionImageAdjuster.TryGetRestorableManualAdjustment(out restorableAdjustment))
                    {
                        using (new EditorGUI.DisabledScope(
                                   AreaLitOcclusionBakeController.IsRunning ||
                                   AreaLitOcclusionJournalStore.HasActiveJournal ||
                                   EditorApplication.isPlayingOrWillChangePlaymode))
                        {
                            if (GUILayout.Button(
                                    "Revert Last Save (" + Path.GetFileName(restorableAdjustment.assetPath) + ")"))
                            {
                                RestoreLastOutputAdjustment();
                            }
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(outputAdjustmentPreviewError))
            {
                EditorGUILayout.HelpBox(outputAdjustmentPreviewError, MessageType.Warning);
            }
            if (!string.IsNullOrEmpty(outputAdjustmentStatus))
            {
                EditorGUILayout.HelpBox(outputAdjustmentStatus, MessageType.Info);
            }
        }

        private void SaveOutputAdjustments(Texture preview)
        {
            try
            {
                var record = AreaLitOcclusionImageAdjuster.ApplyToAssetInPlace(
                    preview,
                    outputBrightness,
                    outputContrast);
                outputBrightness = 1f;
                outputContrast = 1f;
                outputAdjustmentStatus =
                    "Saved adjustments to " + record.assetPath +
                    ". The original is backed up and can be restored with Revert Last Save.";
                Repaint();
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception exception)
            {
                outputAdjustmentStatus = "Could not save adjustments: " + exception.Message;
                Debug.LogException(exception);
            }
        }

        private void RestoreLastOutputAdjustment()
        {
            try
            {
                var record = AreaLitOcclusionImageAdjuster.RestoreLastManualAdjustment();
                outputBrightness = 1f;
                outputContrast = 1f;
                outputPreviewOverride = AssetDatabase.LoadAssetAtPath<Texture>(record.assetPath);
                outputAdjustmentStatus =
                    "Restored the original texture at " + record.assetPath +
                    ". A copy of the adjusted version was also retained in the recovery folder.";
                Repaint();
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception exception)
            {
                outputAdjustmentStatus = "Could not restore the previous texture: " + exception.Message;
                Debug.LogException(exception);
            }
        }

        private Texture GetAutomaticOutputPreviewTexture()
        {
            if (discovery != null)
            {
                foreach (var receiver in discovery.receivers.Where(receiver => receiver.selected))
                {
                    if (receiver.material == null || !receiver.material.HasProperty("_AreaLitOcclusion")) continue;
                    var assigned = receiver.material.GetTexture("_AreaLitOcclusion");
                    if (assigned != null) return assigned;
                }
            }


            var activeScene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(activeScene.path)) return null;
            var folder = AreaLitOcclusionPaths.GetGeneratedFolder(activeScene.path);
            if (!AssetDatabase.IsValidFolder(folder)) return null;

            return AssetDatabase.FindAssets("t:Texture2D", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(AssetDatabase.LoadAssetAtPath<Texture2D>)
                .FirstOrDefault(texture => texture != null);
        }

        private Material GetOutputAdjustmentPreviewMaterial()
        {
            if (outputAdjustmentPreviewMaterial != null) return outputAdjustmentPreviewMaterial;
            if (!string.IsNullOrEmpty(outputAdjustmentPreviewError)) return null;

            try
            {
                outputAdjustmentPreviewMaterial = AreaLitOcclusionImageAdjuster.CreateMaterial();
            }
            catch (Exception exception)
            {
                outputAdjustmentPreviewError = exception.Message;
            }

            return outputAdjustmentPreviewMaterial;
        }

        private void DrawOutput()
        {
            EditorGUILayout.Space(12f);
            EditorGUILayout.LabelField("Stable output", EditorStyles.boldLabel);
            try
            {
                var activeScene = SceneManager.GetActiveScene();
                var path = string.IsNullOrEmpty(activeScene.path)
                    ? "Save the active scene to assign its stable output folder."
                    : AreaLitOcclusionPaths.GetGeneratedFolder(activeScene.path);
                EditorGUILayout.SelectableLabel(path, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            catch (Exception exception)
            {
                EditorGUILayout.HelpBox(exception.Message, MessageType.Warning);
            }
        }

        private void DrawDebugTools()
        {
            EditorGUILayout.Space(12f);
            showDebug = EditorGUILayout.Foldout(showDebug, "Debug & inspection", true);
            if (!showDebug) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Inspect the occlusion setup", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Creates and opens the exact isolated staging copies used by the safe bake. It disables normal lights, colors cloned AreaLit emitter materials, " +
                    "creates isolated Bakery proxies, and pauses before rendering. " +
                    "No object in an original scene is changed.",
                    MessageType.None);

                var reason = GetBlockingReason();
                using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(reason)))
                {
                    if (GUILayout.Button("Prepare Scene for Inspection", GUILayout.Height(28f)))
                    {
                        try
                        {
                            AreaLitOcclusionBakeController.PrepareForInspection(
                                discovery,
                                applyToMaterials,
                                outputBrightness,
                                outputContrast,
                                bakeIntensityMultiplier);
                        }
                        catch (ExitGUIException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            EditorUtility.DisplayDialog("AreaLit Occlusion", exception.Message, "OK");
                            return;
                        }
                        GUIUtility.ExitGUI();
                    }
                }

                if (!string.IsNullOrEmpty(reason))
                {
                    EditorGUILayout.LabelField(reason, EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        private void DrawBakeButton()
        {
            var reason = GetBlockingReason();
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(reason)))
                {
                    if (GUILayout.Button("Bake Occlusion", GUILayout.Height(34f)))
                    {
                        try
                        {
                            AreaLitOcclusionBakeController.BeginBake(
                                discovery,
                                applyToMaterials,
                                outputBrightness,
                                outputContrast,
                                bakeIntensityMultiplier);
                        }
                        catch (ExitGUIException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            EditorUtility.DisplayDialog("AreaLit Occlusion", exception.Message, "OK");
                        }
                    }
                }

                if (!string.IsNullOrEmpty(reason))
                {
                    EditorGUILayout.LabelField(reason, EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        private string GetBlockingReason()
        {
            if (discovery == null) return "Scan the loaded scenes first.";
            if (AreaLitOcclusionBakeController.IsRunning) return "An occlusion bake is already running.";
            if (AreaLitOcclusionJournalStore.HasActiveJournal) return "Restore the active recovery transaction before starting another bake.";
            if (!AreaLitOcclusionBakery.IsAvailable) return AreaLitOcclusionBakery.UnavailableReason;
            if (AreaLitOcclusionBakery.BakeInProgress) return "Bakery is already rendering.";
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Exit Play Mode before baking.";
            if (!discovery.emitters.Any(emitter => emitter.selected)) return "Select at least one AreaLit emitter.";
            if (discovery.emitters.Any(emitter => emitter.selected && !emitter.canAutoProxy))
                return "Every selected AreaLit emitter needs safe proxy geometry.";
            if (discovery.emitters.Any(emitter => emitter.selected && emitter.channel == OcclusionChannel.Alpha))
                return "Alpha is not available in color-lightmap mode; use red, green, or blue.";
            {
                var dirtyMaterial = discovery.receivers
                    .Where(receiver => receiver.selected)
                    .Select(receiver => receiver.material)
                    .FirstOrDefault(material => material != null && EditorUtility.IsDirty(material));
                if (dirtyMaterial != null)
                    return "Save receiver material '" + dirtyMaterial.name + "', or deselect it before baking.";
            }

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                if (AreaLitOcclusionPaths.IsTransactionScene(scene.path))
                    return "Open the original scene before starting a new transaction.";
                if (string.IsNullOrEmpty(scene.path)) return "Save every loaded scene before baking.";
                if (scene.isDirty) return "Save scene '" + scene.name + "' before baking. It will not be auto-saved.";
            }

            return string.Empty;
        }
    }
}
