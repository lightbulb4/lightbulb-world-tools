using System;
using UnityEditor;
using UnityEngine;

namespace Lightbulb.WorldTools
{
    internal static class RendererLightingSettings
    {
        internal static float Scale(MeshRenderer renderer)
        {
            using (var so = new SerializedObject(renderer))
                return so.FindProperty("m_ScaleInLightmap").floatValue;
        }

        // The operation owns Undo so a whole category is one undoable edit.
        internal static void SetScale(MeshRenderer renderer, float scale)
        {
            if (!float.IsFinite(scale) || scale < 0) throw new ArgumentOutOfRangeException(nameof(scale));
            using (var so = new SerializedObject(renderer))
            {
                so.FindProperty("m_ScaleInLightmap").floatValue = scale;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }
    }
}
