using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Xestrel.Core;
using Xestrel.Runtime;

namespace Xestrel.Isolation
{
    /// <summary>
    /// Copies a specified AnimatorController into <c>Assets/Xestrel/&lt;AvatarName&gt;/Animators/</c>,
    /// walks its state machines + blend trees, copies every referenced AnimationClip into
    /// <c>Assets/Xestrel/&lt;AvatarName&gt;/Animations/</c>, and rewires the copy controller to
    /// point at the clip copies. If the original controller is referenced from the avatar's
    /// VRCAvatarDescriptor playable layers, those references are swapped to the copy as well.
    /// On-demand (one controller at a time), like texture isolation.
    /// </summary>
    internal static class AnimatorIsolator
    {
        public static AnimatorController IsolateController(
            XestrelMaterialIsolation state, RuntimeAnimatorController original)
        {
            if (state == null || original == null) return null;

            // Reuse if already isolated.
            if (state.animatorBindings != null)
            {
                foreach (var b in state.animatorBindings)
                {
                    if (b == null || b.original != original) continue;
                    if (b.copy is AnimatorController existing)
                    {
                        TryAutoWire(state, original, existing);
                        return existing;
                    }
                }
            }

            var src = original as AnimatorController;
            if (src == null)
            {
                XestrelLog.Warn(XestrelLogCategory.Isolate,
                    $"AnimatorIsolator: '{original.name}' is not an AnimatorController (AnimatorOverrideControllers are not supported)");
                return null;
            }

            var srcPath = AssetDatabase.GetAssetPath(src);
            if (string.IsNullOrEmpty(srcPath))
            {
                XestrelLog.Warn(XestrelLogCategory.Isolate,
                    $"AnimatorIsolator: '{src.name}' has no asset path; skipping");
                return null;
            }

            var animatorsDir = IsolationPaths.AnimatorsDir(state.avatarName);
            XestrelPaths.EnsureDirectory(animatorsDir);
            var dst = AssetDatabase.GenerateUniqueAssetPath(
                animatorsDir + "/" + XestrelPaths.SanitiseFileSegment(src.name) + ".controller");
            if (!AssetDatabase.CopyAsset(srcPath, dst))
            {
                XestrelLog.Error(XestrelLogCategory.Isolate, $"Animator copy failed: {srcPath} → {dst}");
                return null;
            }
            var copy = AssetDatabase.LoadAssetAtPath<AnimatorController>(dst);

            // Seed clip map from previously recorded bindings so a clip shared across
            // controllers is isolated to a single per-avatar copy.
            var clipMap = new Dictionary<AnimationClip, AnimationClip>();
            if (state.clipBindings != null)
            {
                foreach (var b in state.clipBindings)
                {
                    if (b != null && b.original != null && b.copy != null)
                        clipMap[b.original] = b.copy;
                }
            }

            var animationsDir = IsolationPaths.AnimationsDir(state.avatarName);
            XestrelPaths.EnsureDirectory(animationsDir);

            foreach (var layer in copy.layers)
                RewriteStateMachine(layer.stateMachine, animationsDir, clipMap);

            EditorUtility.SetDirty(copy);

            if (state.animatorBindings == null)
                state.animatorBindings = new List<XestrelAnimatorBinding>();
            state.animatorBindings.Add(new XestrelAnimatorBinding { original = original, copy = copy });

            state.clipBindings = new List<XestrelClipBinding>(clipMap.Count);
            foreach (var kv in clipMap)
                state.clipBindings.Add(new XestrelClipBinding { original = kv.Key, copy = kv.Value });

            EditorUtility.SetDirty(state);
            AssetDatabase.SaveAssets();

            TryAutoWire(state, original, copy);

            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Isolated animator '{src.name}' for '{state.avatarName}': controller→{dst}, {clipMap.Count} clip binding(s) total");

            return copy;
        }

        // Also used by WorkspaceForker with a pre-seeded copy→fork clip map.
        internal static void RewriteStateMachine(
            AnimatorStateMachine sm, string animationsDir, Dictionary<AnimationClip, AnimationClip> clipMap)
        {
            if (sm == null) return;

            foreach (var sc in sm.states)
            {
                var s = sc.state;
                if (s == null) continue;
                var newMotion = RewriteMotion(s.motion, animationsDir, clipMap);
                if (!ReferenceEquals(newMotion, s.motion))
                {
                    s.motion = newMotion;
                    EditorUtility.SetDirty(s);
                }
            }
            foreach (var sub in sm.stateMachines)
                RewriteStateMachine(sub.stateMachine, animationsDir, clipMap);
        }

        private static Motion RewriteMotion(
            Motion motion, string animationsDir, Dictionary<AnimationClip, AnimationClip> clipMap)
        {
            if (motion == null) return null;
            if (motion is AnimationClip clip) return IsolateClip(clip, animationsDir, clipMap);
            if (motion is BlendTree tree)
            {
                var ch = tree.children;
                bool dirty = false;
                for (int i = 0; i < ch.Length; i++)
                {
                    var newM = RewriteMotion(ch[i].motion, animationsDir, clipMap);
                    if (!ReferenceEquals(newM, ch[i].motion))
                    {
                        ch[i].motion = newM;
                        dirty = true;
                    }
                }
                if (dirty)
                {
                    tree.children = ch;
                    EditorUtility.SetDirty(tree);
                }
                return tree;
            }
            return motion;
        }

        private static AnimationClip IsolateClip(
            AnimationClip clip, string animationsDir, Dictionary<AnimationClip, AnimationClip> clipMap)
        {
            if (clip == null) return null;
            if (clipMap.TryGetValue(clip, out var existing)) return existing;

            var srcPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(srcPath))
            {
                XestrelLog.Warn(XestrelLogCategory.Isolate,
                    $"Clip '{clip.name}' has no asset path; leaving reference alone");
                clipMap[clip] = clip;
                return clip;
            }
            if (IsolationPaths.IsUnderIsolationRoot(srcPath))
            {
                clipMap[clip] = clip;
                return clip;
            }
            // Embedded clips inside FBX / model assets cannot be copied in isolation.
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(srcPath);
            if (mainAsset != clip)
            {
                XestrelLog.Warn(XestrelLogCategory.Isolate,
                    $"Clip '{clip.name}' is a sub-asset of {srcPath}; skipping");
                clipMap[clip] = clip;
                return clip;
            }

            var dst = AssetDatabase.GenerateUniqueAssetPath(
                animationsDir + "/" + XestrelPaths.SanitiseFileSegment(clip.name) + ".anim");
            if (!AssetDatabase.CopyAsset(srcPath, dst))
            {
                XestrelLog.Error(XestrelLogCategory.Isolate, $"Clip copy failed: {srcPath} → {dst}");
                clipMap[clip] = clip;
                return clip;
            }
            var copy = AssetDatabase.LoadAssetAtPath<AnimationClip>(dst);
            clipMap[clip] = copy;
            XestrelLog.Info(XestrelLogCategory.Isolate, $"Clip copy: {dst}");
            return copy;
        }

        // Swap any VRCAvatarDescriptor playable-layer reference to `original` over to `copy`.
        // Also used by WorkspaceForker to swap an old copy for its fork.
        internal static void TryAutoWire(
            XestrelMaterialIsolation state,
            RuntimeAnimatorController original,
            RuntimeAnimatorController copy)
        {
            var desc = state.GetComponent<VRCAvatarDescriptor>();
            if (desc == null) return;
            bool dirty = false;
            if (desc.baseAnimationLayers != null)
            {
                for (int i = 0; i < desc.baseAnimationLayers.Length; i++)
                {
                    if (desc.baseAnimationLayers[i].animatorController == original)
                    {
                        if (!dirty) Undo.RecordObject(desc, "Xestrel Isolate Animator");
                        desc.baseAnimationLayers[i].animatorController = copy;
                        dirty = true;
                    }
                }
            }
            if (desc.specialAnimationLayers != null)
            {
                for (int i = 0; i < desc.specialAnimationLayers.Length; i++)
                {
                    if (desc.specialAnimationLayers[i].animatorController == original)
                    {
                        if (!dirty) Undo.RecordObject(desc, "Xestrel Isolate Animator");
                        desc.specialAnimationLayers[i].animatorController = copy;
                        dirty = true;
                    }
                }
            }
            if (dirty) EditorUtility.SetDirty(desc);
        }

        public static void Restore(XestrelMaterialIsolation state)
        {
            if (state == null) return;
            if (state.animatorBindings != null && state.animatorBindings.Count > 0)
            {
                var inverse = new Dictionary<RuntimeAnimatorController, RuntimeAnimatorController>();
                foreach (var b in state.animatorBindings)
                {
                    if (b != null && b.copy != null && b.original != null)
                        inverse[b.copy] = b.original;
                }
                var desc = state.GetComponent<VRCAvatarDescriptor>();
                if (desc != null)
                {
                    bool dirty = false;
                    if (desc.baseAnimationLayers != null)
                    {
                        for (int i = 0; i < desc.baseAnimationLayers.Length; i++)
                        {
                            var ac = desc.baseAnimationLayers[i].animatorController;
                            if (ac != null && inverse.TryGetValue(ac, out var orig))
                            {
                                if (!dirty) Undo.RecordObject(desc, "Xestrel Restore Animator");
                                desc.baseAnimationLayers[i].animatorController = orig;
                                dirty = true;
                            }
                        }
                    }
                    if (desc.specialAnimationLayers != null)
                    {
                        for (int i = 0; i < desc.specialAnimationLayers.Length; i++)
                        {
                            var ac = desc.specialAnimationLayers[i].animatorController;
                            if (ac != null && inverse.TryGetValue(ac, out var orig))
                            {
                                if (!dirty) Undo.RecordObject(desc, "Xestrel Restore Animator");
                                desc.specialAnimationLayers[i].animatorController = orig;
                                dirty = true;
                            }
                        }
                    }
                    if (dirty) EditorUtility.SetDirty(desc);
                }
            }

            Undo.RecordObject(state, "Xestrel Restore Animator");
            state.animatorBindings = new List<XestrelAnimatorBinding>();
            state.clipBindings = new List<XestrelClipBinding>();
            EditorUtility.SetDirty(state);
            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Cleared animator/clip bindings for '{state.avatarName}' (copy assets left on disk)");
        }
    }
}
