using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lightbulb.WorldTools
{
    // Data only, stored on an EditorOnly object and stripped from builds.
    [AddComponentMenu("")]
    public sealed class LightmapVolumeSwapState : MonoBehaviour
    {
        [Serializable] public sealed class Surface
        {
            public MeshRenderer renderer;
            public float scale;
        }
        [Serializable] public sealed class MaterialSettings
        {
            public Material material;
            public float volumes, specularity;
        }
        public bool applied, changeScale, changeVolumes, changeSpecularity;
        public List<Surface> surfaces = new List<Surface>();
        public List<MaterialSettings> materials = new List<MaterialSettings>();
    }
}
