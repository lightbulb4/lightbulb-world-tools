using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lightbulb.WorldTools
{
    internal static class DisabledObjectCleanup
    {
        internal sealed class Entry
        {
            internal GameObject Object;
            internal bool Included;
            internal readonly List<string> Reasons = new List<string>();
            internal bool Candidate => Reasons.Count == 0;
        }

        internal sealed class Scan
        {
            internal Scene Scene;
            internal readonly List<Entry> Entries = new List<Entry>();
            internal readonly List<string> Uncertainties = new List<string>();
        }

        [Serializable]
        internal sealed class TagChange
        {
            public GameObject Object;
            public string OriginalTag;
        }

        internal static Scan Collect(Func<string, bool> cancel = null)
        {
            var scan = new Scan { Scene = SceneMaterials.Active() };
            // Only active-scene objects are candidates. Other loaded scenes can still reference them.
            var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Where(s => s.isLoaded && !EditorSceneManager.IsPreviewScene(s)).ToArray();
            SceneReferenceScan.Result references = SceneReferenceScan.Collect(scenes, cancel);
            scan.Uncertainties.AddRange(references.Uncertainties.OrderBy(s => s, StringComparer.Ordinal));
            var entriesByObject = new Dictionary<GameObject, Entry>();
            foreach (GameObject root in scan.Scene.GetRootGameObjects())
                foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = transform.gameObject;
                    if (go.activeSelf || IsEditorOnly(go)) continue;
                    var entry = new Entry { Object = go };
                    if (WithinSun(go)) entry.Reasons.Add("RenderSettings.sun references this branch.");
                    if (go.GetComponentsInChildren<Transform>(true).Any(t => (t.gameObject.hideFlags & HideFlags.NotEditable) != 0))
                        entry.Reasons.Add("This branch contains an object marked NotEditable.");
                    if (scan.Uncertainties.Count > 0) entry.Reasons.Add("Reference scan is incomplete. See scan issues above.");
                    scan.Entries.Add(entry);
                    entriesByObject.Add(go, entry);
                }
            // Walk each referenced target's ancestors once, instead of comparing every reference to every branch.
            foreach (var reference in references.References)
            {
                GameObject target = SceneReferenceScan.Owner(reference.Target);
                if (target == null || target.scene != scan.Scene) continue;
                GameObject source = SceneReferenceScan.Owner(reference.Source);
                for (Transform ancestor = target.transform; ancestor != null; ancestor = ancestor.parent)
                {
                    if (!entriesByObject.TryGetValue(ancestor.gameObject, out Entry entry)) continue;
                    if (source != null && Within(source, entry.Object)) continue;
                    entry.Reasons.Add(SceneReferenceScan.Describe(reference.Source) + " / " + reference.Property +
                        " → " + SceneReferenceScan.Describe(target));
                }
            }
            foreach (Entry entry in scan.Entries) entry.Included = entry.Candidate;
            return scan;
        }

        private static bool WithinSun(GameObject root) => RenderSettings.sun != null && Within(RenderSettings.sun.gameObject, root);
        internal static bool Within(GameObject child, GameObject root) => child == root || child.transform.IsChildOf(root.transform);
        private static bool IsEditorOnly(GameObject go)
        {
            for (Transform t = go.transform; t != null; t = t.parent) if (t.CompareTag("EditorOnly")) return true;
            return false;
        }

        internal static List<TagChange> Apply(Scan preview, Func<string, bool> cancel = null)
        {
            SceneMaterials.RequireActive(preview.Scene);
            GameObject[] selected = preview.Entries.Where(e => e.Included && e.Candidate).Select(e => e.Object).ToArray();
            if (selected.Any(go => go == null)) throw new InvalidOperationException("An object was removed. Scan again.");
            Scan fresh = Collect(cancel);
            var candidates = new HashSet<GameObject>(fresh.Entries.Where(e => e.Candidate).Select(e => e.Object));
            if (selected.Any(go => !candidates.Contains(go)))
                throw new InvalidOperationException("References or object state changed. Scan again before applying.");
            GameObject[] roots = selected.Where(go => !selected.Any(other => other != go && Within(go, other))).ToArray();
            var changes = roots.Select(go => new TagChange { Object = go, OriginalTag = go.tag }).ToList();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Mark unreferenced disabled objects EditorOnly");
            try
            {
                foreach (TagChange change in changes)
                {
                    Undo.RecordObject(change.Object, "Mark EditorOnly");
                    change.Object.tag = "EditorOnly";
                    PrefabUtility.RecordPrefabInstancePropertyModifications(change.Object);
                }
                if (changes.Count > 0) EditorSceneManager.MarkSceneDirty(preview.Scene);
                Undo.CollapseUndoOperations(group);
                return changes;
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }

        internal static int Restore(List<TagChange> changes)
        {
            Scene scene = SceneMaterials.Active();
            var eligible = changes.Where(c => c.Object != null && c.Object.scene == scene && c.Object.CompareTag("EditorOnly")).ToList();
            // Validate all saved tags before mutating any object.
            var tags = new HashSet<string>(UnityEditorInternal.InternalEditorUtility.tags);
            if (eligible.Any(c => !tags.Contains(c.OriginalTag)))
                throw new InvalidOperationException("An original tag was deleted from the project. Recreate it before restoring.");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Restore original object tags");
            try
            {
                foreach (TagChange change in eligible)
                {
                    Undo.RecordObject(change.Object, "Restore original tag");
                    change.Object.tag = change.OriginalTag;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(change.Object);
                }
                if (eligible.Count > 0) EditorSceneManager.MarkSceneDirty(scene);
                Undo.CollapseUndoOperations(group);
                foreach (TagChange change in eligible) changes.Remove(change);
                return eligible.Count;
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }
    }
}
