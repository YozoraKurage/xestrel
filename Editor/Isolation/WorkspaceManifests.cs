using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Xestrel.Core;
using Xestrel.Runtime;

namespace Xestrel.Isolation
{
    /// <summary>
    /// Keeps each workspace's <see cref="XestrelWorkspaceManifest"/> asset mirroring the
    /// scene component, and rebuilds the component from the manifest when it was lost.
    /// The component is authoritative; <see cref="Sync"/> is called after every mutation,
    /// before the caller's <c>AssetDatabase.SaveAssets()</c>.
    /// </summary>
    internal static class WorkspaceManifests
    {
        /// <summary>
        /// Mirror the component into its workspace manifest, creating the asset on first
        /// call. The manifest is reached through the component's direct reference, so it
        /// keeps working after the workspace folder is renamed or moved.
        /// </summary>
        public static void Sync(XestrelMaterialIsolation state)
        {
            if (state == null || string.IsNullOrEmpty(state.avatarName)) return;

            var manifest = state.workspaceManifest as XestrelWorkspaceManifest;
            if (manifest == null)
            {
                manifest = FindOrCreate(state.avatarName);
                if (manifest == null) return;
                state.workspaceManifest = manifest;
                EditorUtility.SetDirty(state);
            }

            manifest.avatarName = state.avatarName;
            manifest.avatarObjectName = state.gameObject.name;
            manifest.avatarGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(state.gameObject).ToString();

            int ignored = 0;
            manifest.bindings = CloneList(state.bindings,
                b => new XestrelMaterialBinding { original = b.original, copy = b.copy }, null, ref ignored);
            manifest.textureBindings = CloneList(state.textureBindings,
                b => new XestrelTextureBinding { original = b.original, copy = b.copy }, null, ref ignored);
            manifest.animatorBindings = CloneList(state.animatorBindings,
                b => new XestrelAnimatorBinding { original = b.original, copy = b.copy }, null, ref ignored);
            manifest.clipBindings = CloneList(state.clipBindings,
                b => new XestrelClipBinding { original = b.original, copy = b.copy }, null, ref ignored);

            var history = BuildHistoryIndex(manifest);
            foreach (var b in manifest.bindings)
                Remember(manifest, history, "material", b.original, b.copy);
            foreach (var b in manifest.textureBindings)
                Remember(manifest, history, "texture", b.original, b.copy);
            foreach (var b in manifest.animatorBindings)
                Remember(manifest, history, "animator", b.original, b.copy);
            foreach (var b in manifest.clipBindings)
                Remember(manifest, history, "clip", b.original, b.copy);

            EditorUtility.SetDirty(manifest);
        }

        /// <summary>
        /// If the workspace folder was renamed (the manifest asset moved with it), adopt
        /// the folder's current name as the workspace name so new copies keep landing
        /// next to the old ones instead of recreating the stale folder. Call before
        /// <c>IsolationPaths.*Dir(state.avatarName)</c> is used.
        /// </summary>
        public static void HealWorkspaceName(XestrelMaterialIsolation state)
        {
            if (state == null || string.IsNullOrEmpty(state.avatarName)) return;
            var manifest = state.workspaceManifest as XestrelWorkspaceManifest;
            if (manifest == null) return;
            var folder = FolderNameOf(manifest);
            if (folder == null || folder == state.avatarName) return;

            Undo.RecordObject(state, "Xestrel Follow Workspace Rename");
            state.avatarName = folder;
            manifest.avatarName = folder;
            EditorUtility.SetDirty(state);
            EditorUtility.SetDirty(manifest);
            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Workspace folder was renamed; now using Assets/Xestrel/{folder}/");
        }

        /// <summary>
        /// Name of the workspace folder the manifest currently lives in, or null when it
        /// is not directly inside a folder under <c>Assets/Xestrel/</c>.
        /// </summary>
        public static string FolderNameOf(XestrelWorkspaceManifest manifest)
        {
            var path = AssetDatabase.GetAssetPath(manifest);
            if (string.IsNullOrEmpty(path)) return null;
            var dir = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir) || dir == IsolationPaths.Root) return null;
            if (!IsolationPaths.IsUnderIsolationRoot(dir + "/")) return null;
            var name = System.IO.Path.GetFileName(dir);
            // Only a direct child of the root is a workspace folder.
            return IsolationPaths.AvatarDir(name) == dir ? name : null;
        }

        public static List<XestrelWorkspaceManifest> FindAll()
        {
            var result = new List<XestrelWorkspaceManifest>();
            foreach (var guid in AssetDatabase.FindAssets("t:XestrelWorkspaceManifest"))
            {
                var m = AssetDatabase.LoadAssetAtPath<XestrelWorkspaceManifest>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (m != null) result.Add(m);
            }
            return result;
        }

        /// <summary>
        /// Rebuild a lost <see cref="XestrelMaterialIsolation"/> component from a
        /// manifest. Refuses to overwrite a component that already has bindings.
        /// Entries whose copy asset no longer exists are skipped; entries whose original
        /// is missing are kept so the dead-binding UI can surface them. The renderers are
        /// not touched — in the lost-component case they already point at the copies.
        /// </summary>
        public static XestrelMaterialIsolation Recover(GameObject avatarRoot, XestrelWorkspaceManifest manifest)
        {
            if (avatarRoot == null || manifest == null) return null;

            var state = avatarRoot.GetComponent<XestrelMaterialIsolation>();
            if (state != null && HasAnyBinding(state))
            {
                XestrelLog.Warn(XestrelLogCategory.Isolate,
                    "Recover refused: the avatar already has a Xestrel component with bindings");
                return null;
            }
            if (state == null)
            {
                state = Undo.AddComponent<XestrelMaterialIsolation>(avatarRoot);
            }

            Undo.RecordObject(state, "Xestrel Recover Workspace");
            state.avatarName = FolderNameOf(manifest) ?? manifest.avatarName;
            state.workspaceManifest = manifest;

            int dropped = 0;
            state.bindings = CloneList(manifest.bindings,
                b => new XestrelMaterialBinding { original = b.original, copy = b.copy },
                b => b.copy != null, ref dropped);
            state.textureBindings = CloneList(manifest.textureBindings,
                b => new XestrelTextureBinding { original = b.original, copy = b.copy },
                b => b.copy != null, ref dropped);
            state.animatorBindings = CloneList(manifest.animatorBindings,
                b => new XestrelAnimatorBinding { original = b.original, copy = b.copy },
                b => b.copy != null, ref dropped);
            state.clipBindings = CloneList(manifest.clipBindings,
                b => new XestrelClipBinding { original = b.original, copy = b.copy },
                b => b.copy != null, ref dropped);

            EditorUtility.SetDirty(state);
            Sync(state);
            AssetDatabase.SaveAssets();
            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Recovered workspace '{state.avatarName}' onto '{avatarRoot.name}'" +
                (dropped > 0 ? $" ({dropped} binding(s) skipped: copy asset missing)" : ""));
            return state;
        }

        private static bool HasAnyBinding(XestrelMaterialIsolation state) =>
            (state.bindings?.Count ?? 0) + (state.textureBindings?.Count ?? 0) +
            (state.animatorBindings?.Count ?? 0) + (state.clipBindings?.Count ?? 0) > 0;

        private static XestrelWorkspaceManifest FindOrCreate(string avatarName)
        {
            var dir = IsolationPaths.AvatarDir(avatarName);
            var path = dir + "/" + XestrelWorkspaceManifest.FileName;
            var existing = AssetDatabase.LoadAssetAtPath<XestrelWorkspaceManifest>(path);
            if (existing != null) return existing;

            if (XestrelPaths.EnsureDirectory(dir) == null) return null;
            var manifest = ScriptableObject.CreateInstance<XestrelWorkspaceManifest>();
            manifest.avatarName = avatarName;
            AssetDatabase.CreateAsset(manifest, path);
            XestrelLog.Info(XestrelLogCategory.Isolate, $"Workspace manifest created: {path}");
            return manifest;
        }

        private static List<T> CloneList<T>(
            List<T> src, System.Func<T, T> clone, System.Predicate<T> keep, ref int dropped)
            where T : class
        {
            var result = new List<T>(src?.Count ?? 0);
            if (src == null) return result;
            foreach (var item in src)
            {
                if (item == null) continue;
                if (keep != null && !keep(item))
                {
                    dropped++;
                    continue;
                }
                result.Add(clone(item));
            }
            return result;
        }

        private static Dictionary<string, XestrelGuidRecord> BuildHistoryIndex(XestrelWorkspaceManifest manifest)
        {
            var index = new Dictionary<string, XestrelGuidRecord>();
            if (manifest.guidHistory == null) manifest.guidHistory = new List<XestrelGuidRecord>();
            foreach (var r in manifest.guidHistory)
            {
                if (r != null && !string.IsNullOrEmpty(r.copyGuid))
                    index[r.kind + "|" + r.copyGuid] = r;
            }
            return index;
        }

        private static void Remember(
            XestrelWorkspaceManifest manifest, Dictionary<string, XestrelGuidRecord> index,
            string kind, Object original, Object copy)
        {
            if (original == null || copy == null) return;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(original, out var originalGuid, out long _)) return;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(copy, out var copyGuid, out long _)) return;

            var key = kind + "|" + copyGuid;
            if (index.TryGetValue(key, out var record))
            {
                record.originalGuid = originalGuid;
                return;
            }
            record = new XestrelGuidRecord { kind = kind, copyGuid = copyGuid, originalGuid = originalGuid };
            manifest.guidHistory.Add(record);
            index[key] = record;
        }
    }
}
