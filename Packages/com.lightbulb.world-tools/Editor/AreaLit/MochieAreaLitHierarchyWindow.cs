using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using static Lightbulb.AreaLitOcclusion.MochieAreaLitHierarchy;

namespace Lightbulb.AreaLitOcclusion
{
    internal sealed class MochieAreaLitHierarchyWindow : EditorWindow
    {
        internal const string MenuPath = "GameObject/Lightbulb/Configure Mochie AreaLit";
        [SerializeField] private GameObject root;
        [SerializeField] private Toggle toggle;
        [SerializeField] private SlotEdit[] edits = new SlotEdit[AreaLitAuditBindings.ReceiverSlots.Length];
        private AreaLitAuditBindingGroup[] bindings;
        private int materialCount, enabledCount;
        private string status;
        private MessageType severity;
        private Vector2 scroll;

        [MenuItem(MenuPath, true, 10)]
        private static bool Validate(MenuCommand command) =>
            IsHierarchyRoot(command?.context as GameObject ?? Selection.activeGameObject);

        [MenuItem(MenuPath, false, 10)]
        private static void Open(MenuCommand command)
        {
            var window = GetWindow<MochieAreaLitHierarchyWindow>("Mochie AreaLit");
            window.minSize = new Vector2(410, 300);
            window.root = command?.context as GameObject ?? Selection.activeGameObject;
            window.toggle = Toggle.Keep;
            window.edits = new SlotEdit[AreaLitAuditBindings.ReceiverSlots.Length];
            window.status = null;
            window.Refresh();
        }

        private void OnEnable() { Undo.undoRedoPerformed += Refresh; Refresh(); }
        private void OnDisable() => Undo.undoRedoPerformed -= Refresh;
        private void OnHierarchyChange() => Refresh();
        private void OnProjectChange() => Refresh();
        private void OnFocus() => Refresh();

        private void Refresh()
        {
            var materials = Collect(root);
            materialCount = materials.Length;
            enabledCount = materials.Count(m => m.GetInt(ToggleProperty) == 1);
            bindings = AreaLitAuditBindings.ReceiverSlots.Select(slot =>
                AreaLitAuditBindings.Collect(slot.property, slot.label, false, materials)).ToArray();
            Repaint();
        }

        private void OnGUI()
        {
            if (!IsHierarchyRoot(root))
            {
                EditorGUILayout.HelpBox("Right-click a hierarchy object and choose Lightbulb > Configure Mochie AreaLit.", MessageType.Info);
                return;
            }
            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField("Hierarchy root", root, typeof(GameObject), true);
                EditorGUILayout.LabelField($"Compatible materials: {materialCount}    Enabled: {enabledCount}");
                if (materialCount == 0) return;
                EditorGUILayout.HelpBox("Edits affect every object using these shared materials, including objects outside this hierarchy.", MessageType.Info);
                toggle = (Toggle)EditorGUILayout.EnumPopup("AreaLit", toggle);
                for (int i = 0; i < bindings.Length; i++) DrawSlot(i);

                string reason = AreaLitAudit.BlockedReason ?? InvalidAssignment(edits);
                if (reason != null) EditorGUILayout.HelpBox(reason, MessageType.Info);
                bool hasEdits = toggle != Toggle.Keep || edits.Any(edit => edit.action != Assignment.Keep);
                using (new EditorGUI.DisabledScope(!hasEdits || reason != null))
                    if (GUILayout.Button("Apply to hierarchy")) ApplyEdits();
                if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, severity);
            }
        }

        private void ApplyEdits()
        {
            try
            {
                int count = Apply(root, toggle, edits, out var skipped);
                status = $"Changed {count} shared material(s).";
                severity = skipped.Count == 0 ? MessageType.Info : MessageType.Warning;
                if (skipped.Count > 0) status += $" Skipped {skipped.Count}:\n" + string.Join("\n", skipped);
                Refresh();
            }
            catch (InvalidOperationException ex)
            {
                status = ex.Message;
                severity = MessageType.Error;
            }
        }

        private void DrawSlot(int index)
        {
            var binding = bindings[index];
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(binding.members.Count == 0))
            {
                EditorGUILayout.LabelField(binding.label, GUILayout.Width(75));
                edits[index].action = (Assignment)EditorGUILayout.EnumPopup(edits[index].action, GUILayout.Width(75));
                var action = edits[index].action;
                Texture shown = action == Assignment.Keep ? binding.members.FirstOrDefault()?.texture :
                    action == Assignment.Assign ? edits[index].texture : null;
                using (new EditorGUI.DisabledScope(action == Assignment.Clear))
                {
                    EditorGUI.showMixedValue = action == Assignment.Keep && binding.IsDiscrepant;
                    EditorGUI.BeginChangeCheck();
                    var chosen = (Texture)EditorGUILayout.ObjectField(shown, typeof(Texture), false);
                    if (EditorGUI.EndChangeCheck())
                    {
                        edits[index].texture = chosen;
                        edits[index].action = chosen == null ? Assignment.Clear : Assignment.Assign;
                    }
                    EditorGUI.showMixedValue = false;
                }
            }
        }
    }
}
