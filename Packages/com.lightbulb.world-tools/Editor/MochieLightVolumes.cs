using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lightbulb.WorldTools
{
    internal static class MochieLightVolumes
    {
        internal enum Operation { Keep, Set, Add }

        internal sealed class Setting
        {
            internal readonly string Label, Property;
            internal readonly bool Toggle;
            internal Setting(string label, string property, bool toggle = false)
            { Label = label; Property = property; Toggle = toggle; }
        }

        // The Light Volumes section shared by Mochie Standard and Standard Lite (v2.13).
        internal static readonly Setting[] Settings =
        {
            new Setting("Light Volumes", "_LightVolumesToggle", true),
            new Setting("Strength (0–1)", "_LightVolumeStrength"),
            new Setting("Additive Light Volumes", "_AdditiveLightVolumesToggle", true),
            new Setting("Additive strength", "_AdditiveLightVolumeStrength"),
            new Setting("Specular highlights", "_LightVolumeSpecularity", true),
            new Setting("Specular strength", "_LightVolumeSpecularityStrength"),
            new Setting("Bias", "_LightVolumeBias")
        };
        internal const int SpecularStrength = 5;

        internal readonly struct Edit
        {
            internal readonly int Setting;
            internal readonly Operation Operation;
            internal readonly float Value;
            internal Edit(int setting, Operation operation, float value)
            { Setting = setting; Operation = operation; Value = value; }
        }

        internal static bool Supports(Material material) => material != null && material.shader != null &&
            (material.shader.name == "Mochie/Standard" || material.shader.name == "Mochie/Standard Lite");

        internal static List<Material> Collect(Scene scene) => SceneMaterials.Collect(scene).Where(Supports).ToList();

        internal static string Counts(IEnumerable<Material> materials, int settingIndex)
        {
            Setting setting = Settings[settingIndex];
            int zero = 0, one = 0, greater = 0, other = 0, missing = 0;
            foreach (Material material in materials.Where(m => m != null).Distinct())
            {
                if (!material.HasProperty(setting.Property)) { missing++; continue; }
                float value = material.GetFloat(setting.Property);
                if (value == 0) zero++;
                else if (value == 1) one++;
                else if (!setting.Toggle && Finite(value) && value > 1) greater++;
                else other++;
            }
            string text = setting.Toggle ? $"On: {one}    Off: {zero}" : $"0: {zero}    1: {one}    >1: {greater}";
            if (other > 0) text += $"    Other: {other}";
            if (missing > 0) text += $"    Missing property: {missing}";
            return text;
        }

        internal static int Apply(Scene scene, IEnumerable<Edit> edits)
        {
            SceneMaterials.RequireActive(scene);
            if (!SceneMaterials.IsLightingIdle) throw new InvalidOperationException("Wait until Unity and Bakery have finished baking.");
            var requested = edits.Where(e => e.Operation != Operation.Keep).ToArray();
            foreach (Edit edit in requested)
            {
                if (edit.Setting < 0 || edit.Setting >= Settings.Length ||
                    (edit.Operation != Operation.Set && edit.Operation != Operation.Add) || !Finite(edit.Value))
                    throw new ArgumentException("Choose a valid setting and a finite number.");
                if (Settings[edit.Setting].Toggle && (edit.Operation != Operation.Set || (edit.Value != 0 && edit.Value != 1)))
                    throw new ArgumentException("Toggle settings must be On or Off.");
            }
            if (requested.Select(e => e.Setting).Distinct().Count() != requested.Length)
                throw new ArgumentException("Each setting can only be changed once per operation.");
            if (requested.Length == 0) return 0;

            // Resolve the current scene on every operation. Preflight the entire batch before editing assets.
            var changes = new List<(Material material, Edit edit, float value)>();
            foreach (Material material in Collect(scene))
            {
                string reason = EmptyMaterialMaps.CannotEdit(material);
                if (reason != null) throw new InvalidOperationException(material.name + ": " + reason + ". No materials changed.");
                foreach (Edit edit in requested)
                {
                    Setting setting = Settings[edit.Setting];
                    int index = material.shader.FindPropertyIndex(setting.Property);
                    if (index < 0 || (material.shader.GetPropertyType(index) != UnityEngine.Rendering.ShaderPropertyType.Float &&
                        material.shader.GetPropertyType(index) != UnityEngine.Rendering.ShaderPropertyType.Range &&
                        material.shader.GetPropertyType(index) != UnityEngine.Rendering.ShaderPropertyType.Int))
                        throw new InvalidOperationException(material.name + " does not support " + setting.Property +
                            ". Check the installed Mochie version. No materials changed.");
                    float current = material.GetFloat(setting.Property);
                    float value = edit.Operation == Operation.Add ? current + edit.Value : edit.Value;
                    if (!Finite(value)) throw new InvalidOperationException(material.name + ": " + setting.Label + " would not be finite. No materials changed.");
                    if (edit.Setting == 1) value = Mathf.Clamp01(value);
                    if (current != value) changes.Add((material, edit, value));
                }
            }
            var changed = changes.Select(c => c.material).Distinct().ToArray();
            if (changed.Length == 0) return 0;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Edit Mochie Light Volumes in Scene");
            Undo.RecordObjects(changed, "Edit Mochie Light Volumes in Scene");
            foreach (var change in changes)
                change.material.SetFloat(Settings[change.edit.Setting].Property, change.value);
            foreach (Material material in changed)
            {
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssetIfDirty(material);
            }
            Undo.CollapseUndoOperations(group);
            return changed.Length;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
