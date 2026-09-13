using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lightbulb.WorldTools
{
    // Data only. The owner is tagged EditorOnly and is stripped from player/world builds.
    [AddComponentMenu("")]
    public sealed class LightingExperimentState : MonoBehaviour
    {
        public enum Family { Bakery, PointVolume, BakedVolume }
        public enum Mode { Bakery, LightVolumes, Hybrid }
        [Serializable] public sealed class LightState
        {
            public Component component;
            public Component counterpart;
            public Family family;
            public bool included = true;
            public bool gated;
            public bool enabled;
            public bool activeSelf;
            public float intensity;
            public bool bake, shadows, rebakeShadows, bakeIntoProbes;
            public GameObject generatedObject;
            public bool generatedRendererEnabled = true;
        }
        [Serializable] public sealed class SurfaceState
        {
            public Renderer renderer;
            public Terrain terrain;
            public float scale;
            public int lightmapIndex;
            public Vector4 lightmapST;
            public ReceiveGI receiveGI;
            public LightProbeUsage probes;
        }
        [Serializable] public sealed class MaterialState
        {
            public Material original, experiment;
            public List<string> properties = new List<string>();
            public List<float> values = new List<float>();
        }
        [Serializable] public sealed class Assignment
        {
            public Renderer renderer;
            public Material[] original;
        }
        public int schema = 1;
        public Mode mode;
        public List<LightState> lights = new List<LightState>();
        public List<SurfaceState> surfaces = new List<SurfaceState>();
        public List<MaterialState> materials = new List<MaterialState>();
        public List<Assignment> assignments = new List<Assignment>();
        public Component manager;
        public bool managerEnabled;
        public bool managerProbeBlending;
        public bool appliedShadows = true;
        public List<Light> unityLights = new List<Light>();
        public List<bool> unityEnabled = new List<bool>();
        public bool unityGated;
        public GameObject generatedManager;
        public string materialFolder;
        public string backupScene;
        public bool bakeryLights = true, pointLights, bakedVolumes;
        public bool useLightmaps = true, lvDiffuse = true, lvSpecular = true, lvShadows = true;
        public bool keepReflectionProbes;
        public List<ReflectionProbe> reflectionProbes = new List<ReflectionProbe>();
        public List<bool> reflectionEnabled = new List<bool>();
        public bool reflectionGated;
        public bool surfacesGated;
        public bool finished;
        public string status = "Bake required after changing lighting.";
    }
}
