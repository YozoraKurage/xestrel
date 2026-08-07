using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using Xestrel.Runtime;

namespace Xestrel.Detection
{
    internal enum AssetKind
    {
        Material,
        Texture,
        Mesh,
        Animator,
        Clip,
        Menu,
        Shader,
        Model,
        Other,
    }

    /// <summary>One asset referenced from the avatar's hierarchy, with the components using it.</summary>
    internal sealed class SceneAssetRef
    {
        public Object asset;
        public readonly List<string> usedBy = new List<string>();
    }

    /// <summary>
    /// Generic dependency discovery for the graph window. The first ring (scene →
    /// assets) walks every serialized ObjectReference property of every component, so
    /// it needs no per-type knowledge and sees Modular Avatar / VRCFury / audio / mesh
    /// references alike. Asset → asset edges come from the import pipeline via
    /// <see cref="AssetDatabase.GetDependencies(string,bool)"/> (non-recursive), which
    /// is cheap and avoids serializing huge assets like meshes.
    /// </summary>
    internal static class AssetDependencyScanner
    {
        // Prefab-instance plumbing: every component on an instance references its
        // prefab counterparts through these; showing them would add the base prefab
        // as a "dependency" of every single component.
        private static readonly HashSet<string> SkippedProperties = new HashSet<string>
        {
            "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
        };

        /// <summary>
        /// Distinct assets directly referenced by any component under <paramref name="root"/>.
        /// Sub-asset references collapse to their main asset (an FBX is one entry).
        /// Scripts, scene-internal references and xestrel's own bookkeeping are skipped.
        /// </summary>
        public static List<SceneAssetRef> CollectSceneRefs(GameObject root)
        {
            var result = new List<SceneAssetRef>();
            if (root == null) return result;
            var byAsset = new Dictionary<Object, SceneAssetRef>();

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue; // missing script
                if (component is XestrelMaterialIsolation) continue;

                var so = new SerializedObject(component);
                var prop = so.GetIterator();
                bool enterChildren = true;
                while (prop.Next(enterChildren))
                {
                    enterChildren = prop.propertyType != SerializedPropertyType.String;
                    if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (SkippedProperties.Contains(prop.name)) continue;

                    var obj = prop.objectReferenceValue;
                    if (obj == null || obj is MonoScript) continue;
                    var path = AssetDatabase.GetAssetPath(obj);
                    if (string.IsNullOrEmpty(path)) continue; // scene object

                    var main = IsBuiltinPath(path) ? obj : AssetDatabase.LoadMainAssetAtPath(path);
                    if (main == null) continue;
                    if (!byAsset.TryGetValue(main, out var entry))
                    {
                        entry = new SceneAssetRef { asset = main };
                        byAsset[main] = entry;
                        result.Add(entry);
                    }
                    var usage = component.GetType().Name + " \"" + component.gameObject.name + "\"";
                    if (!entry.usedBy.Contains(usage)) entry.usedBy.Add(usage);
                }
            }
            return result;
        }

        /// <summary>
        /// Direct (non-recursive) dependencies of an asset, as distinct main assets.
        /// Scripts and assemblies are filtered out.
        /// </summary>
        public static List<Object> CollectAssetRefs(Object asset)
        {
            var result = new List<Object>();
            if (asset == null) return result;
            var path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path) || IsBuiltinPath(path)) return result;

            var seen = new HashSet<Object>();
            foreach (var dep in AssetDatabase.GetDependencies(path, false))
            {
                if (dep == path) continue;
                if (dep.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase) ||
                    dep.EndsWith(".asmdef", System.StringComparison.OrdinalIgnoreCase) ||
                    dep.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase)) continue;
                var main = AssetDatabase.LoadMainAssetAtPath(dep);
                if (main == null || !seen.Add(main)) continue;
                result.Add(main);
            }
            return result;
        }

        public static bool IsBuiltinPath(string path) =>
            path == "Resources/unity_builtin_extra" ||
            path == "Library/unity default resources";

        public static AssetKind KindOf(Object asset)
        {
            switch (asset)
            {
                case Material _: return AssetKind.Material;
                case Texture _: return AssetKind.Texture;
                case Mesh _: return AssetKind.Mesh;
                case RuntimeAnimatorController _: return AssetKind.Animator;
                case AnimationClip _: return AssetKind.Clip;
                case Shader _: return AssetKind.Shader;
                case GameObject _: return AssetKind.Model; // prefab or model (FBX) main asset
                case VRCExpressionsMenu _: return AssetKind.Menu;
                case VRCExpressionParameters _: return AssetKind.Menu;
            }
            return AssetKind.Other;
        }

        public static string LabelOf(AssetKind kind)
        {
            switch (kind)
            {
                case AssetKind.Material: return "Mat";
                case AssetKind.Texture: return "Tex";
                case AssetKind.Mesh: return "Mesh";
                case AssetKind.Animator: return "Anim";
                case AssetKind.Clip: return "Clip";
                case AssetKind.Menu: return "Menu";
                case AssetKind.Shader: return "Shader";
                case AssetKind.Model: return "Prefab";
                default: return "Other";
            }
        }
    }
}
