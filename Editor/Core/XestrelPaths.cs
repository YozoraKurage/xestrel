using System.IO;
using UnityEditor;

namespace Xestrel.Core
{
    /// <summary>
    /// Filesystem helpers shared by xestrel's editor code.
    /// </summary>
    internal static class XestrelPaths
    {
        /// <summary>
        /// Ensure that an Assets-relative folder exists. Returns the same path on success, null on failure.
        /// Uses System.IO directly so callers can immediately write files into the directory without
        /// waiting for AssetDatabase to flush a deferred CreateFolder call.
        /// </summary>
        public static string EnsureDirectory(string assetsRelativePath)
        {
            if (string.IsNullOrEmpty(assetsRelativePath)) return null;
            assetsRelativePath = assetsRelativePath.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(assetsRelativePath)) return assetsRelativePath;

            var projectRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
            var systemPath = Path.Combine(projectRoot!, assetsRelativePath);
            try { Directory.CreateDirectory(systemPath); }
            catch (System.Exception ex)
            {
                XestrelLog.Error(XestrelLogCategory.Isolate, $"Failed to create directory {assetsRelativePath}: {ex.Message}");
                return null;
            }
            AssetDatabase.ImportAsset(assetsRelativePath, ImportAssetOptions.ForceUpdate);
            return assetsRelativePath;
        }

        /// <summary>
        /// Sanitise a string for use as a folder/file name segment in an Assets-relative path.
        /// Replaces characters that are illegal on Windows filesystems with '_'.
        /// </summary>
        public static string SanitiseFileSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return "_";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = segment.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                foreach (var bad in invalid)
                {
                    if (chars[i] == bad) { chars[i] = '_'; break; }
                }
            }
            return new string(chars);
        }
    }
}
