using UnityEditor;
using UnityEngine;
using Xestrel.Core;

namespace Xestrel.Isolation
{
    /// <summary>
    /// Creates per-avatar copies of a source Material as plain <c>.mat</c> assets
    /// (not Material Variants). The copy is fully independent: changes to the
    /// original after copying are not reflected.
    /// </summary>
    internal static class MaterialCopyFactory
    {
        /// <summary>
        /// Create a plain copy of <paramref name="src"/> at <paramref name="dstAssetPath"/>.
        /// If the path collides, a unique sibling is chosen.
        /// </summary>
        public static Material Create(Material src, string dstAssetPath)
        {
            if (src == null) return null;
            var dir = System.IO.Path.GetDirectoryName(dstAssetPath)?.Replace('\\', '/');
            XestrelPaths.EnsureDirectory(dir);
            dstAssetPath = AssetDatabase.GenerateUniqueAssetPath(dstAssetPath);

            var srcPath = AssetDatabase.GetAssetPath(src);
            if (!string.IsNullOrEmpty(srcPath) && !IsBuiltinAssetPath(srcPath))
            {
                if (!AssetDatabase.CopyAsset(srcPath, dstAssetPath))
                {
                    XestrelLog.Error(XestrelLogCategory.Isolate,
                        $"Material copy failed: {srcPath} → {dstAssetPath}");
                    return null;
                }
                XestrelLog.Info(XestrelLogCategory.Isolate, $"Material copy: {dstAssetPath}");
                return AssetDatabase.LoadAssetAtPath<Material>(dstAssetPath);
            }

            // Built-in materials (Default-Material etc.) can't go through CopyAsset,
            // and in-memory sources have no file to copy. Instantiate, then save.
            var clone = Object.Instantiate(src);
            clone.name = src.name;
            clone.hideFlags = HideFlags.None;
            AssetDatabase.CreateAsset(clone, dstAssetPath);
            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Material copy (built-in / in-memory source): {dstAssetPath}");
            return clone;
        }

        /// <summary>
        /// True for Unity's built-in asset containers, which AssetDatabase.CopyAsset
        /// cannot read from.
        /// </summary>
        private static bool IsBuiltinAssetPath(string assetPath) =>
            assetPath == "Resources/unity_builtin_extra" ||
            assetPath == "Library/unity default resources";
    }
}
