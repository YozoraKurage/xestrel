using UnityEditor;
using UnityEngine;
using Xestrel.Detection;
using Xestrel.Isolation;

namespace Xestrel.UI
{
    internal static class XestrelContextMenu
    {
        private const string IsolateMenuPath = "GameObject/Xestrel/Isolate Materials";
        private const string WindowMenuPath = "GameObject/Xestrel/Open Asset Isolation";

        [MenuItem(IsolateMenuPath, false, 30)]
        private static void IsolateFromMenu(MenuCommand cmd)
        {
            var go = cmd.context as GameObject;
            if (go == null) return;
            var root = AvatarRootRecogniser.ResolveAvatarRoot(go) ?? go;
            Isolator.Isolate(root);
        }

        [MenuItem(IsolateMenuPath, true)]
        private static bool ValidateIsolate(MenuCommand cmd) => cmd.context is GameObject;

        [MenuItem(WindowMenuPath, false, 31)]
        private static void OpenWindowFromMenu(MenuCommand cmd)
        {
            var go = cmd.context as GameObject;
            if (go == null) return;
            var root = AvatarRootRecogniser.ResolveAvatarRoot(go) ?? go;
            XestrelIsolationWindow.OpenFor(root);
        }

        [MenuItem(WindowMenuPath, true)]
        private static bool ValidateOpenWindow(MenuCommand cmd) => cmd.context is GameObject;
    }
}
