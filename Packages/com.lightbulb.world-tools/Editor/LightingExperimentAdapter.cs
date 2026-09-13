using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools
{
    // Optional, version-checked integrations. No vendor source or hard assembly references.
    internal sealed class LightingExperimentAdapter
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        internal readonly Type Point, Volume, Manager, BakeryPoint, BakeryMesh;
        readonly Type hierarchy, backend, volumeTools, pointUtility;
        bool customChanged, shadowsChanged;
        internal static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
        internal LightingExperimentAdapter()
        {
            Point = Require("VRCLightVolumes.PointLightVolumeInstance");
            Volume = Require("VRCLightVolumes.LightVolumeInstance");
            Manager = Require("VRCLightVolumes.LightVolumeManager");
            BakeryPoint = Require("BakeryPointLight");
            BakeryMesh = Require("BakeryLightMesh");
            hierarchy = Require("VRCLightVolumes.HierarchyMenu");
            backend = Require("VRCLightVolumes.LightVolumeManagerEditorBackend");
            volumeTools = Require("VRCLightVolumes.LightVolumeTools");
            pointUtility = Require("VRCLightVolumes.PointLightVolumeEditorUtility");
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(Point.Assembly);
            if (package == null || package.version != "3.0.0-dev.18")
                throw new InvalidOperationException("Lighting experiments currently require Light Volumes 3.0.0-dev.18. Other versions have different activity and baking contracts.");
            foreach (string field in new[] { "Intensity", "Shadows", "RebakeShadows", "BakeIntoProbes", "IsActive", "LightType", "Projection", "Color", "LightSourceSize", "Angle", "Falloff", "Range", "Cookie", "Cubemap" }) RequireField(Point, field);
            foreach (string field in new[] { "Intensity", "Bake", "IsActive", "Color" }) RequireField(Volume, field);
            Method(backend, "ApplySettings", 6); Method(backend, "CopyProxyToUdon", 1);
            Method(backend, "BakeShadowMaps", 1); Method(pointUtility, "Sync", 4);
            Method(volumeTools, "ApplyRuntimeState", 2);
            Method(hierarchy, "CreatePointLightVolume", 1); Method(hierarchy, "CreateLightVolume", 1);
        }
        static Type Require(string name) => Find(name) ?? throw new InvalidOperationException("Missing integration: " + name);
        static FieldInfo RequireField(Type type, string name) => type.GetField(name, Flags) ?? throw new InvalidOperationException("Unsupported " + type.Name + " field: " + name);
        static MethodInfo Method(Type type, string name, int args) => type.GetMethods(Flags).SingleOrDefault(m => m.Name == name && m.GetParameters().Length == args) ?? throw new InvalidOperationException("Unsupported integration method: " + type.Name + "." + name);
        internal static object Get(Component c, string field) => RequireField(c.GetType(), field).GetValue(c);
        internal static T Get<T>(Component c, string field) => (T)Get(c, field);
        internal static void Set(Component c, string field, object value)
        {
            FieldInfo f = RequireField(c.GetType(), field);
            f.SetValue(c, f.FieldType.IsEnum ? Enum.ToObject(f.FieldType, value) : value);
            EditorUtility.SetDirty(c);
            PrefabUtility.RecordPrefabInstancePropertyModifications(c);
        }
        static object Call(Type t, string name, params object[] args)
        {
            try { return Method(t, name, args.Length).Invoke(null, args); }
            catch (TargetInvocationException e) { throw new InvalidOperationException(t.Name + "." + name + ": " + e.InnerException?.Message, e.InnerException ?? e); }
        }
        internal Component Create(bool point, string name)
        {
            Object selection = Selection.activeObject;
            try
            {
                Call(hierarchy, point ? "CreatePointLightVolume" : "CreateLightVolume", new MenuCommand(null));
                GameObject go = Selection.activeGameObject;
                Component result = go != null ? go.GetComponent(point ? Point : Volume) : null;
                if (result == null) throw new InvalidOperationException("Light Volumes did not create a valid component.");
                go.name = name;
                return result;
            }
            finally { Selection.activeObject = selection; }
        }
        internal Component CreateManager(UnityEngine.SceneManagement.Scene scene) => (Component)Call(Require("VRCLightVolumes.LightVolumeSceneSetup"), "CreateManager", scene);
        internal void Sync(Component c)
        {
            if (c.GetType() == Point)
            {
                int changes = (int)Call(pointUtility, "Sync", c, false, false, false);
                customChanged |= (changes & 1) != 0; shadowsChanged |= (changes & 2) != 0;
            }
            else if (c.GetType() == Volume) { Call(volumeTools, "ApplyRuntimeState", c, false); Call(backend, "CopyProxyToUdon", c); }
            else if (c.GetType() == Manager) Call(backend, "CopyProxyToUdon", c);
            Behaviour backing = Backing(c);
            if (backing != null)
            {
                backing.enabled = ((Behaviour)c).enabled;
                EditorUtility.SetDirty(backing);
                PrefabUtility.RecordPrefabInstancePropertyModifications(backing);
            }
        }
        internal static Behaviour Backing(Component c)
        {
            Type utility = Find("UdonSharpEditor.UdonSharpEditorUtility");
            Type proxy = Find("UdonSharp.UdonSharpBehaviour");
            return utility != null && proxy != null && proxy.IsInstanceOfType(c) ? (Behaviour)Call(utility, "GetBackingUdonBehaviour", c) : null;
        }
        internal void Refresh(Component manager)
        {
            if (manager != null) Call(backend, "ApplySettings", manager, true, customChanged, shadowsChanged, true, true);
        }
        internal void BakeShadows(Component manager) => Call(backend, "BakeShadowMaps", manager);
        internal static bool IsBakery(Component c) => c != null && new[] { "BakeryPointLight", "BakeryDirectLight", "BakerySkyLight", "BakeryLightMesh" }.Contains(c.GetType().FullName);
    }
}
