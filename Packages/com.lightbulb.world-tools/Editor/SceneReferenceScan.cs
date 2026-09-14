using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools
{
    // Shared discovery only. The callers decide which dependencies can be changed or removed.
    internal static class SceneReferenceScan
    {
        internal sealed class Reference
        {
            internal Object Source;
            internal Object Target;
            internal string Property;
        }

        internal sealed class Result
        {
            internal readonly HashSet<Object> Objects = new HashSet<Object>();
            internal readonly List<Reference> References = new List<Reference>();
            internal readonly HashSet<string> Uncertainties = new HashSet<string>();
        }

        internal static GameObject Owner(Object value) => value is GameObject go ? go : (value as Component)?.gameObject;
        internal static string Describe(Object value)
        {
            GameObject go = Owner(value);
            if (go == null) return value == null ? "Missing object" : AssetDatabase.GetAssetPath(value) + " / " + value.name;
            return go.scene.name + "/" + AnimationUtility.CalculateTransformPath(go.transform, null) +
                (value is Component ? " (" + value.GetType().Name + ")" : "");
        }

        internal static Result Collect(IEnumerable<Scene> scenes, Func<string, bool> cancel = null)
        {
            var result = new Result();
            var allowed = new HashSet<Scene>(scenes);
            var pending = new Queue<Object>();
            Action<Object> enqueue = value =>
            {
                if (value == null) return;
                GameObject owner = Owner(value);
                if (owner != null && !EditorUtility.IsPersistent(owner) && !allowed.Contains(owner.scene)) return;
                if (result.Objects.Add(value)) pending.Enqueue(value);
            };
            foreach (Scene scene in allowed)
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        enqueue(transform.gameObject);
                        foreach (Component component in transform.GetComponents<Component>())
                        {
                            if (component == null) result.Uncertainties.Add("Missing script on " + Describe(transform.gameObject));
                            else enqueue(component);
                        }
                    }
            if (allowed.Contains(SceneManager.GetActiveScene()))
            {
                enqueue(RenderSettings.skybox);
                enqueue(RenderSettings.customReflectionTexture);
                enqueue(RenderSettings.sun);
                foreach (LightmapData map in LightmapSettings.lightmaps)
                { enqueue(map.lightmapColor); enqueue(map.lightmapDir); enqueue(map.shadowMask); }
            }
            while (pending.Count > 0)
            {
                Object source = pending.Dequeue();
                if (source == null) continue;
                if (cancel != null && cancel(source.name)) throw new OperationCanceledException();
                // These objects have no scene/texture dependencies useful to either caller.
                if (source is MonoScript || source is Texture || source is Mesh || source is AudioClip) continue;
                Action<Object, string> reference = (target, property) =>
                {
                    if (target == null) return;
                    result.References.Add(new Reference { Source = source, Target = target, Property = property });
                    enqueue(target);
                };
                try
                {
                    // Referenced prefab assets also contribute components and child materials.
                    // Enqueue ownership links without reporting them as incoming usage references.
                    if (source is GameObject gameObject)
                    {
                        foreach (Component component in gameObject.GetComponents<Component>())
                        {
                            if (component == null) result.Uncertainties.Add("Missing script on " + Describe(gameObject));
                            else enqueue(component);
                        }
                        foreach (Transform child in gameObject.transform) enqueue(child.gameObject);
                    }
                    using (var serialized = new SerializedObject(source))
                    {
                        SerializedProperty property = serialized.GetIterator();
                        bool enterChildren = true;
                        while (property.Next(enterChildren))
                        {
                            enterChildren = CanContainReferences(property.propertyType);
                            if (enterChildren && property.isArray && property.arraySize > 0)
                                enterChildren = CanContainReferences(property.GetArrayElementAtIndex(0).propertyType);
                            // Ownership and hierarchy are not usage references.
                            if ((source is Component && property.propertyPath == "m_GameObject") ||
                                (source is Transform && (property.propertyPath == "m_Father" || property.propertyPath.StartsWith("m_Children"))) ||
                                (source is GameObject && property.propertyPath.StartsWith("m_Component")))
                            { enterChildren = false; continue; }
                            if (property.propertyType == SerializedPropertyType.ObjectReference)
                                reference(property.objectReferenceValue, property.propertyPath);
                            else if (property.propertyType == SerializedPropertyType.ExposedReference)
                                reference(property.exposedReferenceValue, property.propertyPath);
                        }
                    }
                    ReadUdon(source, reference);
                    // Terrain's native layer/prototype dependencies are not all exposed by SerializedObject.
                    if (source is Terrain terrain)
                    {
                        reference(terrain.terrainData, "Terrain data");
                        reference(terrain.materialTemplate, "Terrain material");
                    }
                    if (source is TerrainData terrainData)
                    {
                        foreach (TerrainLayer layer in terrainData.terrainLayers) reference(layer, "Terrain layer");
                        foreach (Texture texture in terrainData.alphamapTextures) reference(texture, "Terrain alphamap");
                        reference(terrainData.holesTexture, "Terrain holes");
                        reference(terrainData.heightmapTexture, "Terrain heightmap");
                        foreach (TreePrototype tree in terrainData.treePrototypes) reference(tree.prefab, "Terrain tree prefab");
                        foreach (DetailPrototype detail in terrainData.detailPrototypes)
                        {
                            reference(detail.prototype, "Terrain detail prefab");
                            reference(detail.prototypeTexture, "Terrain detail texture");
                        }
                    }
                    if (source is TerrainLayer terrainLayer)
                    {
                        reference(terrainLayer.diffuseTexture, "Terrain diffuse");
                        reference(terrainLayer.normalMapTexture, "Terrain normal");
                        reference(terrainLayer.maskMapTexture, "Terrain mask");
                    }
                    if (source is Animator animator && animator.runtimeAnimatorController != null)
                    {
                        foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips.Distinct())
                        { reference(clip, "Animator clip"); ReadBindings(animator.gameObject, clip, reference); }
                        // Humanoid muscle curves do not identify bones with transform paths.
                        if (animator.isHuman)
                            foreach (Transform child in animator.GetComponentsInChildren<Transform>(true))
                                reference(child, "Humanoid Animator hierarchy");
                    }
                    if (source is Animation animation)
                    {
                        if (animation.clip != null) { reference(animation.clip, "Default animation"); ReadBindings(animation.gameObject, animation.clip, reference); }
                        foreach (AnimationState state in animation)
                        { reference(state.clip, "Animation clip"); ReadBindings(animation.gameObject, state.clip, reference); }
                    }
                    if (source is AnimationClip animationClip)
                    {
                        foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(animationClip))
                            foreach (var key in AnimationUtility.GetObjectReferenceCurve(animationClip, binding))
                                reference(key.value, "Animation object curve: " + binding.path + "/" + binding.propertyName);
                        foreach (var evt in AnimationUtility.GetAnimationEvents(animationClip))
                            reference(evt.objectReferenceParameter, "Animation event: " + evt.functionName);
                    }
                    if (source is PlayableDirector director && director.playableAsset != null)
                    {
                        reference(director.playableAsset, "Playable asset");
                        foreach (PlayableBinding output in director.playableAsset.outputs)
                        {
                            Object target = director.GetGenericBinding(output.sourceObject);
                            reference(target, "Timeline binding: " + output.streamName);
                            // Timeline may animate descendants, including with custom track types.
                            GameObject owner = Owner(target);
                            if (owner != null)
                                foreach (Transform child in owner.GetComponentsInChildren<Transform>(true))
                                    reference(child, "Timeline bound hierarchy: " + output.streamName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Uncertainties.Add("Could not fully inspect " + Describe(source) + ": " + ex.GetBaseException().Message);
                }
            }
            return result;
        }

        private static bool CanContainReferences(SerializedPropertyType type) =>
            type == SerializedPropertyType.Generic || type == SerializedPropertyType.ManagedReference ||
            type == SerializedPropertyType.ObjectReference || type == SerializedPropertyType.ExposedReference;

        private static void ReadBindings(GameObject root, AnimationClip clip, Action<Object, string> reference)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
            {
                // Protect all matches when sibling names are ambiguous, rather than guessing the runtime target.
                IEnumerable<Transform> matches = new[] { root.transform };
                if (!string.IsNullOrEmpty(binding.path))
                    foreach (string segment in binding.path.Split('/'))
                        matches = matches.SelectMany(t => t.Cast<Transform>().Where(c => c.name == segment)).ToArray();
                foreach (Transform target in matches)
                    reference(target, "Animation " + clip.name + ": " + binding.path + "/" + binding.propertyName);
            }
        }

        // Optional SDK adapter, verified against the installed UdonBehaviour.publicVariables contract.
        // Serialized backing references can lag Inspector edits, so inspect the live variable table too.
        private static void ReadUdon(Object source, Action<Object, string> reference)
        {
            if (source.GetType().FullName != "VRC.Udon.UdonBehaviour") return;
            FieldInfo field = source.GetType().GetField("publicVariables");
            object table = field?.GetValue(source);
            if (table == null) throw new InvalidOperationException("Udon public variable table is unavailable.");
            Type contract = table.GetType().GetInterfaces().FirstOrDefault(t => t.Name == "IUdonVariableTable");
            var symbols = contract?.GetProperty("VariableSymbols")?.GetValue(table) as IEnumerable;
            MethodInfo getter = contract?.GetMethods().FirstOrDefault(m => m.Name == "TryGetVariableValue" &&
                !m.IsGenericMethod && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(object).MakeByRefType());
            if (symbols == null || getter == null) throw new InvalidOperationException("Unsupported Udon variable table contract.");
            foreach (string symbol in symbols)
            {
                object[] args = { symbol, null };
                if (!(bool)getter.Invoke(table, args)) throw new InvalidOperationException("Cannot inspect Udon variable " + symbol);
                ReadUdonValue(args[1], "Udon variable: " + symbol, reference, new HashSet<object>());
            }
        }

        private static void ReadUdonValue(object value, string path, Action<Object, string> reference, HashSet<object> visited)
        {
            if (value == null) return;
            if (value is Object obj) { reference(obj, path); return; }
            Type type = value.GetType();
            if (value is string || type.IsPrimitive || type.IsEnum || value is decimal) return;
            if (!visited.Add(value)) return;
            if (type.IsValueType)
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    ReadUdonValue(field.GetValue(value), path + "." + field.Name, reference, visited);
                return;
            }
            if (value is Array array)
            {
                int index = 0;
                foreach (object item in array) ReadUdonValue(item, path + "[" + index++ + "]", reference, visited);
                return;
            }
            throw new InvalidOperationException("Unsupported Udon reference container at " + path + " (" + value.GetType().Name + ").");
        }
    }
}
