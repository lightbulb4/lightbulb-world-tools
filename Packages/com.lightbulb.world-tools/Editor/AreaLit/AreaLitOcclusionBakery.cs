using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.AreaLitOcclusion
{
    internal static class AreaLitOcclusionBakery
    {
#if BAKERY_INCLUDED
        public const bool DefinePresent = true;
#else
        public const bool DefinePresent = false;
#endif

        private const BindingFlags StaticFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags InstanceFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static Delegate finishedRenderDelegate;

        private static Type RenderLightmapType { get { return FindType("ftRenderLightmap"); } }
        private static Type LightmapsType { get { return FindType("ftLightmaps"); } }
        private static Type PointLightType { get { return FindType("BakeryPointLight"); } }
        private static Type DirectLightType { get { return FindType("BakeryDirectLight"); } }
        private static Type SkyLightType { get { return FindType("BakerySkyLight"); } }
        private static Type LightMeshType { get { return FindType("BakeryLightMesh"); } }

        public static bool IsAvailable
        {
            get
            {
                return DefinePresent &&
                       RenderLightmapType != null &&
                       LightmapsType != null &&
                       PointLightType != null &&
                       DirectLightType != null &&
                       SkyLightType != null &&
                       LightMeshType != null;
            }
        }

        public static string UnavailableReason
        {
            get
            {
                if (!DefinePresent)
                {
                    return "Bakery is required for scene preparation and baking. Install Bakery, then let Unity finish compiling.";
                }
                if (RenderLightmapType == null || LightmapsType == null || LightMeshType == null)
                {
                    return "BAKERY_INCLUDED is set, but Bakery's editor assemblies are unavailable. Reinstall Bakery or remove the stale Bakery scripting define.";
                }
                return "This Bakery installation is missing one or more supported light component types.";
            }
        }

        public static bool BakeInProgress
        {
            get { return IsAvailable && GetStaticValue<bool>(RenderLightmapType, "bakeInProgress"); }
        }

        public static bool UserCanceled
        {
            get { return IsAvailable && GetStaticValue<bool>(RenderLightmapType, "userCanceled"); }
        }

        public static string OutputPath
        {
            get
            {
                RequireAvailable();
                return GetStaticValue<string>(RenderLightmapType, "outputPath");
            }
            set
            {
                RequireAvailable();
                SetStaticValue(RenderLightmapType, "outputPath", value);
            }
        }

        public static bool UseScenePath
        {
            get
            {
                RequireAvailable();
                return GetStaticValue<bool>(RenderLightmapType, "useScenePath");
            }
            set
            {
                RequireAvailable();
                SetStaticValue(RenderLightmapType, "useScenePath", value);
            }
        }

        public static object GetProjectSettings()
        {
            RequireAvailable();
            return InvokeStatic(LightmapsType, "GetProjectSettings");
        }

        public static bool GetDeletePreviousLightmaps(object settings)
        {
            return settings != null && GetInstanceValue<bool>(settings, "deletePreviousLightmapsBeforeBake");
        }

        public static void SetDeletePreviousLightmaps(object settings, bool value)
        {
            if (settings != null) SetInstanceValue(settings, "deletePreviousLightmapsBeforeBake", value);
        }

        public static object GetOrOpenRenderWindow()
        {
            RequireAvailable();
            var instance = GetStaticValue<object>(RenderLightmapType, "instance");
            return instance ?? EditorWindow.GetWindow(RenderLightmapType);
        }

        public static void LoadRenderSettings(object renderWindow)
        {
            RequireAvailable();
            InvokeInstance(renderWindow, "LoadRenderSettings");
        }

        public static void StartRender(object renderWindow)
        {
            RequireAvailable();
            InvokeInstance(renderWindow, "RenderButton", false);
        }

        public static string GetRuntimePath()
        {
            RequireAvailable();
            return (string)InvokeStatic(LightmapsType, "GetRuntimePath");
        }

        public static IEnumerable<Component> FindBakeryLights()
        {
            RequireAvailable();
            return FindComponents(PointLightType)
                .Concat(FindComponents(DirectLightType))
                .Concat(FindComponents(SkyLightType));
        }

        public static IEnumerable<Component> FindBakeryLightMeshes()
        {
            RequireAvailable();
            return FindComponents(LightMeshType);
        }

        public static Component AddLightMeshProxy(GameObject proxyObject, Color color, float intensity)
        {
            RequireAvailable();
            if (proxyObject == null) throw new ArgumentNullException("proxyObject");

            var proxy = proxyObject.AddComponent(LightMeshType);
            SetInstanceValue(proxy, "cutoff", 100f);
            SetInstanceValue(proxy, "samples", 256);
            SetInstanceValue(proxy, "samples2", 16);
            SetInstanceValue(proxy, "samples2_previous", 16);
            SetInstanceValue(proxy, "selfShadow", true);
            SetInstanceValue(proxy, "bakeToIndirect", true);
            SetInstanceValue(proxy, "UID", Guid.NewGuid().GetHashCode());
            SetInstanceValue(proxy, "color", color);
            SetInstanceValue(proxy, "intensity", intensity);
            SetInstanceValue(proxy, "enabled", true);
            return proxy;
        }

        public static void SubscribeFinished(EventHandler handler)
        {
            RequireAvailable();
            if (handler == null) throw new ArgumentNullException("handler");
            if (finishedRenderDelegate != null) return;

            var eventInfo = RenderLightmapType.GetEvent("OnFinishedFullRender", StaticFlags);
            if (eventInfo == null)
            {
                throw new MissingMemberException(RenderLightmapType.FullName, "OnFinishedFullRender");
            }
            finishedRenderDelegate = Delegate.CreateDelegate(
                eventInfo.EventHandlerType,
                handler.Target,
                handler.Method);
            eventInfo.AddEventHandler(null, finishedRenderDelegate);
        }

        public static void UnsubscribeFinished()
        {
            if (finishedRenderDelegate == null || RenderLightmapType == null) return;
            var eventInfo = RenderLightmapType.GetEvent("OnFinishedFullRender", StaticFlags);
            if (eventInfo != null) eventInfo.RemoveEventHandler(null, finishedRenderDelegate);
            finishedRenderDelegate = null;
        }

        public static void RequireAvailable()
        {
            if (!IsAvailable) throw new InvalidOperationException(UnavailableReason);
        }

        private static IEnumerable<Component> FindComponents(Type componentType)
        {
            return UnityEngine.Object.FindObjectsOfType(componentType, true).OfType<Component>();
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        private static object InvokeStatic(Type type, string methodName, params object[] arguments)
        {
            var method = FindMethod(type, methodName, StaticFlags, arguments);
            return method.Invoke(null, arguments);
        }

        private static object InvokeInstance(object instance, string methodName, params object[] arguments)
        {
            if (instance == null) throw new ArgumentNullException("instance");
            var method = FindMethod(instance.GetType(), methodName, InstanceFlags, arguments);
            return method.Invoke(instance, arguments);
        }

        private static MethodInfo FindMethod(
            Type type,
            string methodName,
            BindingFlags flags,
            object[] arguments)
        {
            var method = type.GetMethods(flags)
                .FirstOrDefault(candidate =>
                    candidate.Name == methodName &&
                    ParametersAccept(candidate.GetParameters(), arguments));
            if (method == null) throw new MissingMethodException(type.FullName, methodName);
            return method;
        }

        private static bool ParametersAccept(ParameterInfo[] parameters, object[] arguments)
        {
            if (parameters.Length != arguments.Length) return false;
            for (var index = 0; index < parameters.Length; index++)
            {
                var argument = arguments[index];
                if (argument == null)
                {
                    if (parameters[index].ParameterType.IsValueType &&
                        Nullable.GetUnderlyingType(parameters[index].ParameterType) == null)
                    {
                        return false;
                    }
                    continue;
                }
                if (!parameters[index].ParameterType.IsInstanceOfType(argument)) return false;
            }
            return true;
        }

        private static T GetStaticValue<T>(Type type, string memberName)
        {
            return ConvertValue<T>(GetValue(type, null, memberName, StaticFlags));
        }

        private static T GetInstanceValue<T>(object instance, string memberName)
        {
            if (instance == null) throw new ArgumentNullException("instance");
            return ConvertValue<T>(GetValue(instance.GetType(), instance, memberName, InstanceFlags));
        }

        private static void SetStaticValue(Type type, string memberName, object value)
        {
            SetValue(type, null, memberName, value, StaticFlags);
        }

        private static void SetInstanceValue(object instance, string memberName, object value)
        {
            if (instance == null) throw new ArgumentNullException("instance");
            SetValue(instance.GetType(), instance, memberName, value, InstanceFlags);
        }

        private static object GetValue(Type type, object instance, string memberName, BindingFlags flags)
        {
            var field = type.GetField(memberName, flags);
            if (field != null) return field.GetValue(instance);
            var property = type.GetProperty(memberName, flags);
            if (property != null) return property.GetValue(instance, null);
            throw new MissingMemberException(type.FullName, memberName);
        }

        private static void SetValue(
            Type type,
            object instance,
            string memberName,
            object value,
            BindingFlags flags)
        {
            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                field.SetValue(instance, value);
                return;
            }
            var property = type.GetProperty(memberName, flags);
            if (property != null)
            {
                property.SetValue(instance, value, null);
                return;
            }
            throw new MissingMemberException(type.FullName, memberName);
        }

        private static T ConvertValue<T>(object value)
        {
            if (value == null) return default(T);
            if (value is T) return (T)value;
            return (T)Convert.ChangeType(value, typeof(T));
        }
    }
}
