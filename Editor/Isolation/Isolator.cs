using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Xestrel.Core;
using Xestrel.Runtime;

namespace Xestrel.Isolation
{
    /// <summary>
    /// Top-level entry point used by the context menu / window's "Isolate" button.
    /// Only materials are isolated in bulk; texture isolation is on-demand from the
    /// per-property "Isolate" button in the window (see <see cref="TextureIsolator.IsolateProperty"/>).
    /// Restore reverts both, in dependency order: textures first (writes into copy
    /// materials), then materials (renderers point back at originals).
    /// </summary>
    internal static class Isolator
    {
        public static void Isolate(GameObject avatarRoot)
        {
            if (avatarRoot == null) return;
            MaterialIsolator.Isolate(avatarRoot);
        }

        public static void Restore(XestrelMaterialIsolation state)
        {
            if (state == null) return;
            AnimatorIsolator.Restore(state);
            TextureIsolator.Restore(state);
            MaterialIsolator.Restore(state);
            WorkspaceManifests.Sync(state);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Drop bindings whose original or copy asset no longer exists (e.g. the user
        /// deleted a copy from the Project window). Returns the number removed.
        /// </summary>
        public static int PruneDeadBindings(XestrelMaterialIsolation state)
        {
            if (state == null) return 0;

            int before =
                (state.bindings?.Count ?? 0) +
                (state.textureBindings?.Count ?? 0) +
                (state.animatorBindings?.Count ?? 0) +
                (state.clipBindings?.Count ?? 0);

            Undo.RecordObject(state, "Xestrel Prune Bindings");
            state.bindings?.RemoveAll(b => b == null || b.original == null || b.copy == null);
            state.textureBindings?.RemoveAll(b => b == null || b.original == null || b.copy == null);
            state.animatorBindings?.RemoveAll(b => b == null || b.original == null || b.copy == null);
            state.clipBindings?.RemoveAll(b => b == null || b.original == null || b.copy == null);

            int after =
                (state.bindings?.Count ?? 0) +
                (state.textureBindings?.Count ?? 0) +
                (state.animatorBindings?.Count ?? 0) +
                (state.clipBindings?.Count ?? 0);

            int removed = before - after;
            if (removed > 0)
            {
                EditorUtility.SetDirty(state);
                // The manifest's guidHistory keeps the pruned copy→original GUID pairs,
                // so the relationship stays reconstructible if the asset comes back.
                WorkspaceManifests.Sync(state);
                AssetDatabase.SaveAssets();
                XestrelLog.Info(XestrelLogCategory.Isolate,
                    $"Pruned {removed} dead binding(s) on '{state.avatarName}'");
            }
            return removed;
        }

        /// <summary>
        /// True if any binding references a missing (deleted) original or copy asset.
        /// </summary>
        public static bool HasDeadBindings(XestrelMaterialIsolation state)
        {
            if (state == null) return false;
            return AnyDead(state.bindings, b => b.original == null || b.copy == null) ||
                   AnyDead(state.textureBindings, b => b.original == null || b.copy == null) ||
                   AnyDead(state.animatorBindings, b => b.original == null || b.copy == null) ||
                   AnyDead(state.clipBindings, b => b.original == null || b.copy == null);
        }

        private static bool AnyDead<T>(List<T> list, System.Predicate<T> isDead) where T : class
        {
            if (list == null) return false;
            foreach (var b in list)
            {
                if (b == null || isDead(b)) return true;
            }
            return false;
        }
    }
}
