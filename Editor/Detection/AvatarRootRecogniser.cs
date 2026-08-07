using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Xestrel.Detection
{
    /// <summary>
    /// Helpers for resolving the "avatar root" GameObject a user is acting on.
    /// </summary>
    internal static class AvatarRootRecogniser
    {
        /// <summary>
        /// True if <paramref name="go"/> has a VRCAvatarDescriptor directly on it.
        /// </summary>
        public static bool HasAvatarDescriptor(GameObject go)
        {
            if (go == null) return false;
            return go.GetComponent<VRCAvatarDescriptor>() != null;
        }

        /// <summary>
        /// Resolve the GameObject xestrel should treat as the avatar root for <paramref name="go"/>.
        /// Walks up the hierarchy to the first ancestor (or self) that has a VRCAvatarDescriptor.
        /// Falls back to the outermost prefab instance root if present, else null.
        /// </summary>
        public static GameObject ResolveAvatarRoot(GameObject go)
        {
            if (go == null) return null;
            for (var t = go.transform; t != null; t = t.parent)
            {
                if (t.GetComponent<VRCAvatarDescriptor>() != null) return t.gameObject;
            }
            if (PrefabUtility.IsPartOfPrefabInstance(go))
                return PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            return null;
        }
    }
}
