using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Xestrel.Runtime;

namespace Xestrel.Isolation
{
    /// <summary>
    /// Read-only reference walks over an avatar and its workspace folder, shared by the
    /// Isolated tab and the graph window: which assets the avatar actually references,
    /// and which copies in the workspace folder nothing references any more.
    /// </summary>
    internal static class WorkspaceAudit
    {
        /// <summary>Distinct materials on the avatar's renderers, hierarchy order.</summary>
        public static List<Material> CollectRendererMaterials(GameObject root)
        {
            var result = new List<Material>();
            if (root == null) return result;
            var seen = new HashSet<Material>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m != null && seen.Add(m)) result.Add(m);
                }
            }
            return result;
        }

        /// <summary>Distinct textures on the material's visible texture properties.</summary>
        public static List<Texture> CollectMaterialTextures(Material material)
        {
            var result = new List<Texture>();
            var shader = material != null ? material.shader : null;
            if (shader == null) return result;
            var seen = new HashSet<Texture>();
            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                if (ShaderUtil.IsShaderPropertyHidden(shader, i)) continue;
                var tex = material.GetTexture(ShaderUtil.GetPropertyName(shader, i));
                if (tex != null && seen.Add(tex)) result.Add(tex);
            }
            return result;
        }

        /// <summary>Distinct controllers on the avatar's descriptor playable layers.</summary>
        public static List<RuntimeAnimatorController> CollectDescriptorControllers(GameObject root)
        {
            var result = new List<RuntimeAnimatorController>();
            var desc = root != null ? root.GetComponent<VRCAvatarDescriptor>() : null;
            if (desc == null) return result;
            var seen = new HashSet<RuntimeAnimatorController>();
            AddLayers(desc.baseAnimationLayers, seen, result);
            AddLayers(desc.specialAnimationLayers, seen, result);
            return result;
        }

        private static void AddLayers(VRCAvatarDescriptor.CustomAnimLayer[] layers,
            HashSet<RuntimeAnimatorController> seen, List<RuntimeAnimatorController> result)
        {
            if (layers == null) return;
            foreach (var l in layers)
            {
                if (l.animatorController != null && seen.Add(l.animatorController))
                    result.Add(l.animatorController);
            }
        }

        /// <summary>Distinct clips reachable from the controller's states and blend trees.</summary>
        public static List<AnimationClip> CollectControllerClips(RuntimeAnimatorController controller)
        {
            var result = new List<AnimationClip>();
            var seen = new HashSet<AnimationClip>();
            if (controller is AnimatorController ac)
            {
                foreach (var layer in ac.layers)
                    CollectStateMachineClips(layer.stateMachine, seen, result);
            }
            else if (controller != null)
            {
                foreach (var clip in controller.animationClips)
                {
                    if (clip != null && seen.Add(clip)) result.Add(clip);
                }
            }
            return result;
        }

        private static void CollectStateMachineClips(
            AnimatorStateMachine sm, HashSet<AnimationClip> seen, List<AnimationClip> result)
        {
            if (sm == null) return;
            foreach (var sc in sm.states)
            {
                if (sc.state != null) CollectMotionClips(sc.state.motion, seen, result);
            }
            foreach (var sub in sm.stateMachines)
                CollectStateMachineClips(sub.stateMachine, seen, result);
        }

        private static void CollectMotionClips(Motion motion, HashSet<AnimationClip> seen, List<AnimationClip> result)
        {
            if (motion is AnimationClip clip)
            {
                if (seen.Add(clip)) result.Add(clip);
                return;
            }
            if (motion is BlendTree tree)
            {
                foreach (var child in tree.children)
                    CollectMotionClips(child.motion, seen, result);
            }
        }

        /// <summary>
        /// Copies in the workspace folder that neither the avatar references (renderers,
        /// material textures, descriptor controllers and their clips) nor any binding
        /// still tracks — i.e. leftovers from Restore or manual edits. The manifest
        /// asset and folders are excluded. Deleting them is left to the user.
        /// </summary>
        public static List<Object> CollectUnusedCopies(XestrelMaterialIsolation state)
        {
            var result = new List<Object>();
            if (state == null || string.IsNullOrEmpty(state.avatarName)) return result;
            var dir = IsolationPaths.AvatarDir(state.avatarName);
            if (!AssetDatabase.IsValidFolder(dir)) return result;

            var referenced = new HashSet<Object>();
            foreach (var m in CollectRendererMaterials(state.gameObject))
            {
                referenced.Add(m);
                foreach (var t in CollectMaterialTextures(m)) referenced.Add(t);
            }
            foreach (var c in CollectDescriptorControllers(state.gameObject))
            {
                referenced.Add(c);
                foreach (var clip in CollectControllerClips(c)) referenced.Add(clip);
            }
            AddBindingCopies(referenced, state);

            var seenPaths = new HashSet<string>();
            foreach (var guid in AssetDatabase.FindAssets("", new[] { dir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!seenPaths.Add(path)) continue;
                if (AssetDatabase.IsValidFolder(path)) continue;
                if (path.EndsWith("/" + XestrelWorkspaceManifest.FileName, System.StringComparison.Ordinal)) continue;
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null || referenced.Contains(asset)) continue;
                result.Add(asset);
            }
            return result;
        }

        private static void AddBindingCopies(HashSet<Object> set, XestrelMaterialIsolation state)
        {
            if (state.bindings != null)
                foreach (var b in state.bindings) { if (b != null && b.copy != null) set.Add(b.copy); }
            if (state.textureBindings != null)
                foreach (var b in state.textureBindings) { if (b != null && b.copy != null) set.Add(b.copy); }
            if (state.animatorBindings != null)
                foreach (var b in state.animatorBindings) { if (b != null && b.copy != null) set.Add(b.copy); }
            if (state.clipBindings != null)
                foreach (var b in state.clipBindings) { if (b != null && b.copy != null) set.Add(b.copy); }
        }
    }
}
