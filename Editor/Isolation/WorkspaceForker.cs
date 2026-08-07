using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Xestrel.Core;
using Xestrel.Runtime;

namespace Xestrel.Isolation
{
    /// <summary>
    /// Turns a duplicated isolated avatar into an independent variant. The duplicate
    /// arrives sharing every copy asset with its source, so edits would bleed between
    /// the two. Fork re-copies each bound material / texture / animator / clip into a
    /// fresh workspace folder — inheriting all edits made so far — rewires this
    /// avatar's renderers and descriptor to the forks, and rewrites the bindings so
    /// each fork still maps back to the true shared original. The source avatar and
    /// its workspace are never touched.
    /// </summary>
    internal static class WorkspaceForker
    {
        /// <summary>
        /// One-click variant: duplicate the avatar in the scene (keeping its prefab
        /// connection when possible) and fork the duplicate, so the new avatar gets
        /// independent copies inheriting all edits made so far. The source avatar and
        /// its workspace are never touched. Returns the duplicate, or null when the
        /// duplication itself failed.
        /// </summary>
        public static GameObject DuplicateAndFork(XestrelMaterialIsolation state)
        {
            if (state == null) return null;
            var src = state.gameObject;

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();

            var dup = DuplicateInScene(src);
            if (dup == null) return null;

            var dupState = dup.GetComponent<XestrelMaterialIsolation>();
            if (dupState == null)
            {
                // Should not happen — the duplicate carries the component with it.
                XestrelLog.Error(XestrelLogCategory.Isolate,
                    $"Duplicate of '{src.name}' has no isolation component; not forking");
                return dup;
            }
            if (!Fork(dupState))
            {
                XestrelLog.Warn(XestrelLogCategory.Isolate,
                    $"Duplicated '{src.name}' but Fork failed; the duplicate still shares the source's copies");
            }
            Undo.SetCurrentGroupName("Xestrel Duplicate & Fork");
            Undo.CollapseUndoOperations(undoGroup);
            return dup;
        }

        // Duplicate like Ctrl+D so a prefab instance stays connected to its prefab; fall
        // back to a plain Instantiate (which flattens the prefab link) when the
        // pasteboard path is unavailable (e.g. batch mode).
        private static GameObject DuplicateInScene(GameObject src)
        {
            try
            {
                Selection.activeGameObject = src;
                Unsupported.DuplicateGameObjectsUsingPasteboard();
                var dup = Selection.activeGameObject;
                if (dup != null && dup != src) return dup;
            }
            catch (System.Exception ex)
            {
                XestrelLog.Warn(XestrelLogCategory.Isolate,
                    $"Pasteboard duplicate unavailable ({ex.Message}); falling back to Instantiate");
            }

            var fallback = Object.Instantiate(src, src.transform.parent);
            fallback.name = GameObjectUtility.GetUniqueNameForSibling(src.transform.parent, src.name);
            Undo.RegisterCreatedObjectUndo(fallback, "Xestrel Duplicate & Fork");
            return fallback;
        }

        public static bool Fork(XestrelMaterialIsolation state)
        {
            if (state == null) return false;

            Undo.RecordObject(state, "Xestrel Fork Workspace");
            var oldName = state.avatarName;
            state.avatarName = null;
            MaterialIsolator.EnsureWorkspaceName(state, state.gameObject);
            if (string.IsNullOrEmpty(state.avatarName))
            {
                state.avatarName = oldName;
                return false;
            }
            // Still pointing at the source workspace's manifest; drop it so Sync creates
            // a fresh manifest inside the forked folder.
            state.workspaceManifest = null;

            var matMap = ForkMaterials(state);
            var texMap = ForkTextures(state);
            RewireForkedMaterialTextures(state, texMap);
            ForkAnimators(state);
            RewireRenderers(state, matMap);

            EditorUtility.SetDirty(state);
            WorkspaceManifests.Sync(state);
            AssetDatabase.SaveAssets();
            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Forked workspace '{oldName}' → '{state.avatarName}': " +
                $"{matMap.Count} material(s), {texMap.Count} texture(s) re-copied");
            return true;
        }

        private static Dictionary<Material, Material> ForkMaterials(XestrelMaterialIsolation state)
        {
            var map = new Dictionary<Material, Material>();
            if (state.bindings == null) return map;
            var dir = IsolationPaths.MaterialsDir(state.avatarName);
            foreach (var b in state.bindings)
            {
                if (b == null || b.copy == null) continue;
                if (map.TryGetValue(b.copy, out var existing))
                {
                    b.copy = existing;
                    continue;
                }
                var dst = dir + "/" + XestrelPaths.SanitiseFileSegment(b.copy.name) + ".mat";
                var fork = MaterialCopyFactory.Create(b.copy, dst);
                if (fork == null) continue;
                map[b.copy] = fork;
                b.copy = fork;
            }
            return map;
        }

        private static Dictionary<Texture, Texture> ForkTextures(XestrelMaterialIsolation state)
        {
            var map = new Dictionary<Texture, Texture>();
            if (state.textureBindings == null) return map;
            var dir = IsolationPaths.TexturesDir(state.avatarName);
            foreach (var b in state.textureBindings)
            {
                if (b == null || b.copy == null) continue;
                if (map.TryGetValue(b.copy, out var existing))
                {
                    b.copy = existing;
                    continue;
                }
                var dst = dir + "/" + XestrelPaths.SanitiseFileSegment(b.copy.name);
                var fork = TextureCopyFactory.Create(b.copy, dst);
                if (fork == null)
                {
                    XestrelLog.Warn(XestrelLogCategory.Isolate,
                        $"Fork: texture '{b.copy.name}' could not be re-copied; it stays shared with the source workspace");
                    continue;
                }
                map[b.copy] = fork;
                b.copy = fork;
            }
            return map;
        }

        // The forked materials were byte-copies of the source copies, so their texture
        // slots still point at the source workspace's texture copies. Repoint them.
        private static void RewireForkedMaterialTextures(
            XestrelMaterialIsolation state, Dictionary<Texture, Texture> texMap)
        {
            if (state.bindings == null || texMap.Count == 0) return;
            foreach (var b in state.bindings)
            {
                if (b == null || b.copy == null) continue;
                var shader = b.copy.shader;
                if (shader == null) continue;
                bool dirty = false;
                int count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    var propName = ShaderUtil.GetPropertyName(shader, i);
                    var tex = b.copy.GetTexture(propName);
                    if (tex == null || !texMap.TryGetValue(tex, out var fork)) continue;
                    b.copy.SetTexture(propName, fork);
                    dirty = true;
                }
                if (dirty) EditorUtility.SetDirty(b.copy);
            }
        }

        private static void ForkAnimators(XestrelMaterialIsolation state)
        {
            if (state.animatorBindings == null || state.animatorBindings.Count == 0) return;

            // Fork the clip copies first so the controller rewrite can map to them.
            var clipMap = new Dictionary<AnimationClip, AnimationClip>();
            var animationsDir = IsolationPaths.AnimationsDir(state.avatarName);
            if (state.clipBindings != null)
            {
                foreach (var cb in state.clipBindings)
                {
                    if (cb == null || cb.copy == null) continue;
                    if (clipMap.TryGetValue(cb.copy, out var existing))
                    {
                        cb.copy = existing;
                        continue;
                    }
                    var srcPath = AssetDatabase.GetAssetPath(cb.copy);
                    // Clips recorded as "kept shared" (embedded in FBX etc.) stay shared.
                    if (string.IsNullOrEmpty(srcPath) ||
                        !IsolationPaths.IsUnderIsolationRoot(srcPath)) continue;

                    XestrelPaths.EnsureDirectory(animationsDir);
                    var dst = AssetDatabase.GenerateUniqueAssetPath(
                        animationsDir + "/" + XestrelPaths.SanitiseFileSegment(cb.copy.name) + ".anim");
                    if (!AssetDatabase.CopyAsset(srcPath, dst))
                    {
                        XestrelLog.Error(XestrelLogCategory.Isolate, $"Fork: clip copy failed: {srcPath} → {dst}");
                        continue;
                    }
                    var fork = AssetDatabase.LoadAssetAtPath<AnimationClip>(dst);
                    clipMap[cb.copy] = fork;
                    cb.copy = fork;
                }
            }

            var animatorsDir = IsolationPaths.AnimatorsDir(state.avatarName);
            foreach (var ab in state.animatorBindings)
            {
                if (ab == null || !(ab.copy is AnimatorController srcCtrl)) continue;
                var srcPath = AssetDatabase.GetAssetPath(srcCtrl);
                if (string.IsNullOrEmpty(srcPath)) continue;

                XestrelPaths.EnsureDirectory(animatorsDir);
                var dst = AssetDatabase.GenerateUniqueAssetPath(
                    animatorsDir + "/" + XestrelPaths.SanitiseFileSegment(srcCtrl.name) + ".controller");
                if (!AssetDatabase.CopyAsset(srcPath, dst))
                {
                    XestrelLog.Error(XestrelLogCategory.Isolate, $"Fork: controller copy failed: {srcPath} → {dst}");
                    continue;
                }
                var fork = AssetDatabase.LoadAssetAtPath<AnimatorController>(dst);
                foreach (var layer in fork.layers)
                    AnimatorIsolator.RewriteStateMachine(layer.stateMachine, animationsDir, clipMap);
                EditorUtility.SetDirty(fork);

                // Swap this avatar's descriptor references from the shared copy to the fork.
                AnimatorIsolator.TryAutoWire(state, srcCtrl, fork);
                ab.copy = fork;
            }
        }

        private static void RewireRenderers(
            XestrelMaterialIsolation state, Dictionary<Material, Material> matMap)
        {
            if (matMap.Count == 0) return;
            foreach (var r in state.gameObject.GetComponentsInChildren<Renderer>(true))
            {
                var arr = r.sharedMaterials;
                bool dirty = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] != null && matMap.TryGetValue(arr[i], out var fork))
                    {
                        arr[i] = fork;
                        dirty = true;
                    }
                }
                if (dirty)
                {
                    Undo.RecordObject(r, "Xestrel Fork Workspace");
                    r.sharedMaterials = arr;
                    EditorUtility.SetDirty(r);
                }
            }
        }
    }
}
