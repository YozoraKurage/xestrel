using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Xestrel.Core;
using Xestrel.Runtime;

namespace Xestrel.Isolation
{
    /// <summary>
    /// Walks an avatar's Renderer hierarchy, replaces each shared Material with a
    /// per-avatar copy under <c>Assets/Xestrel/&lt;AvatarName&gt;/Materials/</c>, and
    /// records the mapping on a <see cref="XestrelMaterialIsolation"/> component.
    /// Idempotent: calling <see cref="Isolate"/> twice on the same avatar is a no-op.
    /// </summary>
    internal static class MaterialIsolator
    {
        public static void Isolate(GameObject avatarRoot)
        {
            if (avatarRoot == null)
            {
                XestrelLog.Warn(XestrelLogCategory.Isolate, "Isolate called with null avatarRoot");
                return;
            }

            var state = avatarRoot.GetComponent<XestrelMaterialIsolation>();
            if (state == null)
            {
                state = Undo.AddComponent<XestrelMaterialIsolation>(avatarRoot);
            }
            EnsureWorkspaceName(state, avatarRoot);

            var materialsDir = IsolationPaths.MaterialsDir(state.avatarName);
            XestrelPaths.EnsureDirectory(materialsDir);

            // Build original→copy map from existing bindings.
            var map = new Dictionary<Material, Material>();
            if (state.bindings != null)
            {
                foreach (var b in state.bindings)
                {
                    if (b != null && b.original != null && b.copy != null)
                        map[b.original] = b.copy;
                }
            }

            var renderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            int rewriteCount = 0;
            foreach (var r in renderers)
            {
                var arr = r.sharedMaterials;
                bool dirty = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    var m = arr[i];
                    if (m == null) continue;

                    // Already an isolated copy → leave as-is (keeps Isolate idempotent).
                    var path = AssetDatabase.GetAssetPath(m);
                    if (IsolationPaths.IsUnderIsolationRoot(path)) continue;

                    if (!map.TryGetValue(m, out var copy) || copy == null)
                    {
                        var dst = materialsDir + "/" +
                                  XestrelPaths.SanitiseFileSegment(m.name) + "__xestrel.mat";
                        copy = MaterialCopyFactory.Create(m, dst);
                        if (copy == null) continue;
                        map[m] = copy;
                    }
                    arr[i] = copy;
                    dirty = true;
                }
                if (dirty)
                {
                    Undo.RecordObject(r, "Xestrel Isolate Materials");
                    r.sharedMaterials = arr;
                    EditorUtility.SetDirty(r);
                    rewriteCount++;
                }
            }

            // Rebuild bindings from the (now updated) map.
            Undo.RecordObject(state, "Xestrel Isolate Materials");
            state.bindings = new List<XestrelMaterialBinding>(map.Count);
            foreach (var kv in map)
            {
                state.bindings.Add(new XestrelMaterialBinding { original = kv.Key, copy = kv.Value });
            }
            EditorUtility.SetDirty(state);
            WorkspaceManifests.Sync(state);
            AssetDatabase.SaveAssets();

            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Isolated '{state.avatarName}': {map.Count} unique copies, {rewriteCount} renderers rewritten");
        }

        /// <summary>
        /// Assign the workspace (folder) name once, when the state component is fresh.
        /// The name sticks even if the GameObject is later renamed, so copies keep
        /// landing in the same folder. If a workspace folder with that name already
        /// exists (same-named avatar in another scene, or leftovers from a removed
        /// component), a " (n)" suffix keeps this avatar's assets separate.
        /// </summary>
        internal static void EnsureWorkspaceName(XestrelMaterialIsolation state, GameObject avatarRoot)
        {
            if (state == null || avatarRoot == null) return;
            if (!string.IsNullOrEmpty(state.avatarName))
            {
                // Follow a workspace folder rename before the name is used to build paths.
                WorkspaceManifests.HealWorkspaceName(state);
                return;
            }

            var baseName = XestrelPaths.SanitiseFileSegment(avatarRoot.name);
            var candidate = baseName;
            int suffix = 1;
            while (AssetDatabase.IsValidFolder(IsolationPaths.Root + "/" + candidate))
                candidate = $"{baseName} ({suffix++})";

            Undo.RecordObject(state, "Xestrel Isolate");
            state.avatarName = candidate;
            EditorUtility.SetDirty(state);
            if (candidate != avatarRoot.name)
            {
                XestrelLog.Info(XestrelLogCategory.Isolate,
                    $"Workspace folder for '{avatarRoot.name}' is Assets/Xestrel/{candidate}/");
            }
        }

        /// <summary>
        /// Count the distinct materials on <paramref name="avatarRoot"/>'s renderers that
        /// are not yet per-avatar copies. Used by the UI to preview what Isolate would do.
        /// </summary>
        public static int CountPendingMaterials(GameObject avatarRoot) =>
            CollectPendingMaterials(avatarRoot).Count;

        /// <summary>
        /// Collect the materials on <paramref name="avatarRoot"/>'s renderers that are not
        /// yet per-avatar copies, with the number of renderer slots each one occupies.
        /// Insertion order follows the renderer hierarchy.
        /// </summary>
        public static List<KeyValuePair<Material, int>> CollectPendingMaterials(GameObject avatarRoot)
        {
            var result = new List<KeyValuePair<Material, int>>();
            if (avatarRoot == null) return result;

            var slotCounts = new Dictionary<Material, int>();
            foreach (var r in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    if (IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(m))) continue;
                    if (slotCounts.TryGetValue(m, out var n))
                    {
                        slotCounts[m] = n + 1;
                    }
                    else
                    {
                        slotCounts[m] = 1;
                        result.Add(new KeyValuePair<Material, int>(m, 0));
                    }
                }
            }
            for (int i = 0; i < result.Count; i++)
                result[i] = new KeyValuePair<Material, int>(result[i].Key, slotCounts[result[i].Key]);
            return result;
        }

        /// <summary>
        /// Isolate a single material: create (or reuse) its per-avatar copy and rewire
        /// only the renderer slots that reference it. Adds the state component when
        /// missing, like the bulk <see cref="Isolate"/>. Returns the copy, the material
        /// itself when it is already a copy, or null on failure.
        /// </summary>
        public static Material IsolateSingle(GameObject avatarRoot, Material material)
        {
            if (avatarRoot == null || material == null) return null;
            if (IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(material)))
                return material;

            var state = avatarRoot.GetComponent<XestrelMaterialIsolation>();
            if (state == null)
            {
                state = Undo.AddComponent<XestrelMaterialIsolation>(avatarRoot);
            }
            EnsureWorkspaceName(state, avatarRoot);

            // Reuse an existing copy if one was recorded earlier.
            Material copy = null;
            if (state.bindings != null)
            {
                foreach (var b in state.bindings)
                {
                    if (b != null && b.original == material && b.copy != null)
                    {
                        copy = b.copy;
                        break;
                    }
                }
            }

            if (copy == null)
            {
                var materialsDir = IsolationPaths.MaterialsDir(state.avatarName);
                XestrelPaths.EnsureDirectory(materialsDir);
                var dst = materialsDir + "/" +
                          XestrelPaths.SanitiseFileSegment(material.name) + "__xestrel.mat";
                copy = MaterialCopyFactory.Create(material, dst);
                if (copy == null) return null;

                Undo.RecordObject(state, "Xestrel Isolate Material");
                if (state.bindings == null) state.bindings = new List<XestrelMaterialBinding>();
                state.bindings.Add(new XestrelMaterialBinding { original = material, copy = copy });
                EditorUtility.SetDirty(state);
            }

            int rewriteCount = 0;
            foreach (var r in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                var arr = r.sharedMaterials;
                bool dirty = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] == material)
                    {
                        arr[i] = copy;
                        dirty = true;
                    }
                }
                if (dirty)
                {
                    Undo.RecordObject(r, "Xestrel Isolate Material");
                    r.sharedMaterials = arr;
                    EditorUtility.SetDirty(r);
                    rewriteCount++;
                }
            }
            WorkspaceManifests.Sync(state);
            AssetDatabase.SaveAssets();

            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Isolated material '{material.name}' on '{state.avatarName}': {rewriteCount} renderer(s) rewritten");
            return copy;
        }

        /// <summary>
        /// Revert a single material binding: every renderer slot referencing the copy is
        /// pointed back at the original and the binding is removed. The copy asset stays
        /// on disk.
        /// </summary>
        public static void RestoreBinding(XestrelMaterialIsolation state, XestrelMaterialBinding binding)
        {
            if (state == null || binding == null || binding.copy == null || binding.original == null) return;

            var renderers = state.gameObject.GetComponentsInChildren<Renderer>(true);
            int rewriteCount = 0;
            foreach (var r in renderers)
            {
                var arr = r.sharedMaterials;
                bool dirty = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] == binding.copy)
                    {
                        arr[i] = binding.original;
                        dirty = true;
                    }
                }
                if (dirty)
                {
                    Undo.RecordObject(r, "Xestrel Restore Material");
                    r.sharedMaterials = arr;
                    EditorUtility.SetDirty(r);
                    rewriteCount++;
                }
            }

            Undo.RecordObject(state, "Xestrel Restore Material");
            state.bindings?.Remove(binding);
            EditorUtility.SetDirty(state);
            WorkspaceManifests.Sync(state);
            AssetDatabase.SaveAssets();
            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Restored material '{binding.original.name}' on '{state.avatarName}': {rewriteCount} renderer(s) reverted (copy asset left on disk)");
        }

        public static void Restore(XestrelMaterialIsolation state)
        {
            if (state == null) return;

            // Build copy→original map.
            var inverse = new Dictionary<Material, Material>();
            if (state.bindings != null)
            {
                foreach (var b in state.bindings)
                {
                    if (b != null && b.copy != null && b.original != null)
                        inverse[b.copy] = b.original;
                }
            }
            if (inverse.Count == 0)
            {
                state.bindings = new List<XestrelMaterialBinding>();
                EditorUtility.SetDirty(state);
                return;
            }

            var renderers = state.gameObject.GetComponentsInChildren<Renderer>(true);
            int rewriteCount = 0;
            foreach (var r in renderers)
            {
                var arr = r.sharedMaterials;
                bool dirty = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] == null) continue;
                    if (inverse.TryGetValue(arr[i], out var orig))
                    {
                        arr[i] = orig;
                        dirty = true;
                    }
                }
                if (dirty)
                {
                    Undo.RecordObject(r, "Xestrel Restore Materials");
                    r.sharedMaterials = arr;
                    EditorUtility.SetDirty(r);
                    rewriteCount++;
                }
            }

            Undo.RecordObject(state, "Xestrel Restore Materials");
            state.bindings = new List<XestrelMaterialBinding>();
            EditorUtility.SetDirty(state);
            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Restored '{state.avatarName}': {rewriteCount} renderers reverted (copy assets left on disk)");
        }
    }
}
