using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Lightbulb.AreaLitOcclusion
{
    [InitializeOnLoad]
    internal static class AreaLitAuditFixes
    {
        private const string MipUndoKey = "Lightbulb.AreaLitAudit.MipUndo.";

        static AreaLitAuditFixes()
        {
            Undo.undoRedoEvent += OnUndoRedo;
        }

        private static void OnUndoRedo(in UndoRedoInfo info)
        {
            if (!info.undoName.StartsWith("AreaLit audit: ", StringComparison.Ordinal)) return;
            var guid = SessionState.GetString(MipUndoKey + info.undoGroup, "");
            if (string.IsNullOrEmpty(guid)) return;
            var texture = AssetDatabase.LoadAssetAtPath<RenderTexture>(AssetDatabase.GUIDToAssetPath(guid));
            // Like Unity's RenderTexture inspector, invalidate the GPU allocation on Undo/Redo.
            // SessionState survives assembly reloads, but expires with the editor's Undo history.
            if (texture != null) texture.Release();
        }

        public static string UnavailableReason(AreaLitAuditFinding finding)
        {
            if (finding.fix == AreaLitAuditFix.None) return "This finding needs inspection or a design choice; no automatic fix is offered.";
            if (finding.fix == AreaLitAuditFix.SceneLayers) return AreaLitAuditLayers.UnavailableReason(finding.layerChange);
            if (finding.fix == AreaLitAuditFix.OrderCaptureCamera)
            {
                var camera = finding.target as Camera;
                if (camera == null || EditorUtility.IsPersistent(camera) || !camera.gameObject.scene.IsValid() || !camera.gameObject.scene.isLoaded)
                    return "The capture camera must belong to a loaded original scene.";
                if (float.IsNaN(finding.proposedCameraDepth) || float.IsInfinity(finding.proposedCameraDepth) ||
                    finding.proposedCameraDepth < -100 || finding.proposedCameraDepth > 100)
                    return "Set this custom camera order manually in the Inspector; the proposed depth is outside -100 to 100.";
                if (!string.IsNullOrEmpty(camera.gameObject.scene.path) && !AssetDatabase.IsOpenForEdit(camera.gameObject.scene.path))
                    return "Check out the camera's scene before applying this change.";
                return null;
            }
            var texture = finding.target as Texture;
            if (texture == null) return "The texture is no longer available. Run the audit again.";
            var writeReason = AssetWriteBlockReason(texture);
            if (writeReason != null) return writeReason;
            if ((finding.fix == AreaLitAuditFix.EnableMipmaps || finding.fix == AreaLitAuditFix.DisableMipmaps) && !(texture is RenderTexture))
                return "Mip-chain allocation can only be changed here for render textures.";
            var rt = texture as RenderTexture;
            if (finding.fix == AreaLitAuditFix.EnableMipmaps && (rt.dimension != UnityEngine.Rendering.TextureDimension.Tex2D ||
                rt.antiAliasing != 1 || rt.graphicsFormat == UnityEngine.Experimental.Rendering.GraphicsFormat.None))
                return "Resolve texture dimension, multisampling and color format in the Inspector before enabling automatic color mipmaps.";
            return null;
        }

        internal static string AssetWriteBlockReason(UnityEngine.Object asset)
        {
            if (asset == null) return "The asset is no longer available. Run the audit again.";
            var path = AssetDatabase.GetAssetPath(asset);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                return "Fixes are limited to project-owned assets under Assets. Package and temporary objects are inspection-only.";
            if (!AssetDatabase.IsMainAsset(asset) || !AssetDatabase.IsNativeAsset(asset))
                return "Imported objects and subassets are inspection-only. Edit their source/import settings or use an editable standalone asset.";
            if (!AssetDatabase.IsOpenForEdit(path, StatusQueryOptions.UseCachedIfPossible))
                return "The asset is not open for editing. Check it out before applying a fix.";
            var absolutePath = Path.GetFullPath(path);
            if (!File.Exists(absolutePath) || (File.GetAttributes(absolutePath) & FileAttributes.ReadOnly) != 0)
                return "The asset file is missing or read-only.";
            return null;
        }

        public static string ChangeDescription(AreaLitAuditFinding finding)
        {
            switch (finding.fix)
            {
                case AreaLitAuditFix.SceneLayers:
                    return AreaLitAuditLayers.ChangeDescription(finding.layerChange);
                case AreaLitAuditFix.EnableMipmaps:
                    return "Enable mipmaps and automatic mip generation. This increases texture memory and mip-generation work; a live render-texture buffer will be recreated.";
                case AreaLitAuditFix.DisableMipmaps:
                    return "Disable mipmaps on the emitter-data texture. A live buffer will be recreated; resolution, format and emitter capacity stay unchanged.";
                case AreaLitAuditFix.Trilinear:
                    return "Set filtering to Trilinear. Preserve anisotropy, mip bias and every other texture setting.";
                case AreaLitAuditFix.ResetMipBias:
                    return "Reset mip bias to 0. Reflections may look softer. No other texture settings change.";
                case AreaLitAuditFix.OrderCaptureCamera:
                    return "Set the emitter-data camera's Depth to " + finding.proposedCameraDepth +
                           " so it renders before the linked indirect-bounce capture(s). Bounce and player cameras are unchanged. This also changes its order relative to other cameras.";
                default: throw new ArgumentOutOfRangeException(nameof(finding));
            }
        }

        public static void Apply(AreaLitAuditFinding requested, bool includeDisabled)
        {
            if (requested.fix == AreaLitAuditFix.SceneLayers)
            {
                AreaLitAuditLayers.Apply(requested, includeDisabled);
                return;
            }
            // Revalidate the rule and editability after confirmation; stale rows never authorize a change.
            var current = AreaLitAudit.Scan(includeDisabled).findings.FirstOrDefault(item => item.id == requested.id && item.fix == requested.fix);
            if (current == null) throw new InvalidOperationException("The finding changed or no longer applies. Run the audit again.");
            var reason = UnavailableReason(current);
            if (reason != null) throw new InvalidOperationException(reason);
            if (current.fix == AreaLitAuditFix.OrderCaptureCamera)
            {
                if (current.proposedCameraDepth != requested.proposedCameraDepth)
                    throw new InvalidOperationException("The proposed camera order changed. Run the audit again.");
                var camera = (Camera)current.target;
                Undo.IncrementCurrentGroup();
                var cameraUndoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Order AreaLit capture cameras");
                Undo.RegisterCompleteObjectUndo(camera, "Order AreaLit capture cameras");
                camera.depth = current.proposedCameraDepth;
                PrefabUtility.RecordPrefabInstancePropertyModifications(camera);
                EditorSceneManager.MarkSceneDirty(camera.gameObject.scene);
                Undo.CollapseUndoOperations(cameraUndoGroup);
                Undo.IncrementCurrentGroup();
                return;
            }
            var texture = (Texture)current.target;
            var rt = texture as RenderTexture;
            var rebuild = rt != null && rt.IsCreated() &&
                          (current.fix == AreaLitAuditFix.EnableMipmaps || current.fix == AreaLitAuditFix.DisableMipmaps);
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("AreaLit audit: " + current.title);
            Undo.RegisterCompleteObjectUndo(texture, "AreaLit audit texture fix");
            try
            {
                if (rebuild) rt.Release();
                switch (current.fix)
                {
                    case AreaLitAuditFix.EnableMipmaps:
                        rt.useMipMap = true;
                        rt.autoGenerateMips = true;
                        break;
                    case AreaLitAuditFix.DisableMipmaps:
                        rt.useMipMap = false;
                        break;
                    case AreaLitAuditFix.Trilinear:
                        texture.filterMode = FilterMode.Trilinear;
                        break;
                    case AreaLitAuditFix.ResetMipBias:
                        texture.mipMapBias = 0;
                        break;
                    default: throw new ArgumentOutOfRangeException();
                }
                if (rebuild && !rt.Create()) throw new InvalidOperationException("Unity could not recreate the render texture.");
                EditorUtility.SetDirty(texture);
                if (rt != null && (current.fix == AreaLitAuditFix.EnableMipmaps || current.fix == AreaLitAuditFix.DisableMipmaps))
                    SessionState.SetString(MipUndoKey + group, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(rt)));
                Undo.CollapseUndoOperations(group);
                Undo.IncrementCurrentGroup();
            }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                if (rebuild && !rt.IsCreated() && !rt.Create())
                    Debug.LogError("AreaLit audit rolled back the settings, but Unity could not recreate the original render texture.", rt);
                throw;
            }
        }
    }
}
