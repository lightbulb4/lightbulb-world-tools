using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.WorldTools
{
    internal static class SceneMaterials
    {
        internal static Scene Active()
        {
            if (!MaterialTextureBatch.IsIdle || PrefabStageUtility.GetCurrentPrefabStage() != null)
                throw new InvalidOperationException("Open a scene outside Prefab Mode and Play Mode, and wait until Unity is idle.");
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                throw new InvalidOperationException("There is no loaded active scene.");
            return scene;
        }

        internal static void RequireActive(Scene expected)
        {
            if (Active() != expected) throw new InvalidOperationException("The active scene changed. Scan again before applying.");
        }

        internal static List<Material> Collect(Scene scene, Func<string, bool> cancel = null)
        {
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                throw new InvalidOperationException("The scanned scene is no longer loaded.");
            var materials = new HashSet<Material>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (cancel != null && cancel(root.name)) throw new OperationCanceledException();
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                    foreach (Material material in renderer.sharedMaterials)
                        if (material != null) materials.Add(material);
                foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                    if (terrain.materialTemplate != null) materials.Add(terrain.materialTemplate);
                foreach (Skybox skybox in root.GetComponentsInChildren<Skybox>(true))
                    if (skybox.material != null) materials.Add(skybox.material);
            }
            if (scene == SceneManager.GetActiveScene() && RenderSettings.skybox != null) materials.Add(RenderSettings.skybox);
            return materials.OrderBy(m => AssetDatabase.GetAssetPath(m), StringComparer.Ordinal).ThenBy(m => m.name).ToList();
        }
    }
}
