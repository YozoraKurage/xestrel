using UnityEditor;
using UnityEngine;
using Xestrel.Core;

namespace Xestrel.Isolation
{
    /// <summary>
    /// Creates per-avatar copies of a source Texture as a duplicated asset file.
    /// Uses <see cref="AssetDatabase.CopyAsset"/> so the importer settings on the
    /// duplicate match the source (compression, sRGB flag, mip settings, etc.).
    /// </summary>
    internal static class TextureCopyFactory
    {
        /// <summary>
        /// Create a copy of <paramref name="src"/> next to <paramref name="dstAssetPath"/>.
        /// Returns null when the source has no on-disk asset (RenderTexture, sub-asset
        /// of an FBX, in-memory texture), in which case we leave the reference alone.
        /// </summary>
        public static Texture Create(Texture src, string dstAssetPath)
        {
            if (src == null) return null;

            var srcPath = AssetDatabase.GetAssetPath(src);
            if (string.IsNullOrEmpty(srcPath))
            {
                XestrelLog.Warn(XestrelLogCategory.Isolate,
                    $"Texture has no asset path; skipping copy: {src.name}");
                return null;
            }

            // Sub-asset of a model (e.g. embedded FBX textures): AssetDatabase.CopyAsset
            // would duplicate the entire containing asset. We can't isolate cleanly.
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(srcPath);
            if (mainAsset != src)
            {
                XestrelLog.Warn(XestrelLogCategory.Isolate,
                    $"Texture is a sub-asset of {srcPath}; skipping (cannot copy in isolation)");
                return null;
            }

            var dir = System.IO.Path.GetDirectoryName(dstAssetPath)?.Replace('\\', '/');
            XestrelPaths.EnsureDirectory(dir);

            // Preserve the source extension so importer settings carry over correctly.
            var srcExt = System.IO.Path.GetExtension(srcPath);
            if (!string.IsNullOrEmpty(srcExt))
            {
                var dstNoExt = System.IO.Path.ChangeExtension(dstAssetPath, null);
                dstAssetPath = dstNoExt + srcExt;
            }
            dstAssetPath = AssetDatabase.GenerateUniqueAssetPath(dstAssetPath);

            if (!AssetDatabase.CopyAsset(srcPath, dstAssetPath))
            {
                XestrelLog.Error(XestrelLogCategory.Isolate,
                    $"Texture copy failed: {srcPath} → {dstAssetPath}");
                return null;
            }
            XestrelLog.Info(XestrelLogCategory.Isolate, $"Texture copy: {dstAssetPath}");
            return AssetDatabase.LoadAssetAtPath<Texture>(dstAssetPath);
        }
    }
}
