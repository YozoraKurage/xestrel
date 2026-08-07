using Xestrel.Core;

namespace Xestrel.Isolation
{
    /// <summary>
    /// Path conventions for isolated material copies: <c>Assets/Xestrel/&lt;AvatarName&gt;/Materials/</c>.
    /// </summary>
    internal static class IsolationPaths
    {
        public const string Root = "Assets/Xestrel";

        public static string AvatarDir(string avatarName) =>
            Root + "/" + XestrelPaths.SanitiseFileSegment(avatarName);

        public static string MaterialsDir(string avatarName) =>
            AvatarDir(avatarName) + "/Materials";

        public static string TexturesDir(string avatarName) =>
            AvatarDir(avatarName) + "/Textures";

        public static string AnimatorsDir(string avatarName) =>
            AvatarDir(avatarName) + "/Animators";

        public static string AnimationsDir(string avatarName) =>
            AvatarDir(avatarName) + "/Animations";

        /// <summary>
        /// True if the given asset path lives under the isolated materials folder for any avatar.
        /// </summary>
        public static bool IsUnderIsolationRoot(string assetPath) =>
            !string.IsNullOrEmpty(assetPath) &&
            assetPath.StartsWith(Root + "/", System.StringComparison.Ordinal);
    }
}
