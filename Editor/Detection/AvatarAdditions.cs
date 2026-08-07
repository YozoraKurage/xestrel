using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Xestrel.Runtime;

namespace Xestrel.Detection
{
    /// <summary>
    /// What has been added to (or removed from) the avatar relative to its base prefab.
    /// </summary>
    internal sealed class AvatarAdditionsReport
    {
        public bool isPrefabInstance;
        public GameObject basePrefab;
        public readonly List<GameObject> addedObjects = new List<GameObject>();
        public readonly List<Component> addedComponents = new List<Component>();
        public readonly List<string> removedComponentDescriptions = new List<string>();
    }

    /// <summary>
    /// Derived (never stored) view of the avatar's structure. Additions are computed
    /// from the scene's prefab overrides on every call, so the view can never drift
    /// from the scene. When the avatar is not a prefab instance (unpacked), it degrades
    /// to listing child prefab instances — scene-built objects are indistinguishable
    /// from the original hierarchy in that case.
    /// </summary>
    internal static class AvatarAdditions
    {
        public static AvatarAdditionsReport Scan(GameObject avatarRoot)
        {
            var report = new AvatarAdditionsReport();
            if (avatarRoot == null) return report;

            if (PrefabUtility.IsAnyPrefabInstanceRoot(avatarRoot) &&
                !PrefabUtility.IsPrefabAssetMissing(avatarRoot))
            {
                report.isPrefabInstance = true;
                report.basePrefab = PrefabUtility.GetCorrespondingObjectFromSource(avatarRoot);

                foreach (var added in PrefabUtility.GetAddedGameObjects(avatarRoot))
                {
                    if (added.instanceGameObject != null)
                        report.addedObjects.Add(added.instanceGameObject);
                }
                foreach (var added in PrefabUtility.GetAddedComponents(avatarRoot))
                {
                    var c = added.instanceComponent;
                    if (c == null) continue;
                    // xestrel's own state component is infrastructure, not a user addition.
                    if (c is XestrelMaterialIsolation) continue;
                    report.addedComponents.Add(c);
                }
                foreach (var removed in PrefabUtility.GetRemovedComponents(avatarRoot))
                {
                    var owner = removed.containingInstanceGameObject != null
                        ? removed.containingInstanceGameObject.name
                        : "?";
                    var type = removed.assetComponent != null
                        ? removed.assetComponent.GetType().Name
                        : "(missing)";
                    report.removedComponentDescriptions.Add(owner + " · " + type);
                }
            }
            else
            {
                foreach (Transform child in avatarRoot.transform)
                {
                    if (PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject))
                        report.addedObjects.Add(child.gameObject);
                }
            }
            return report;
        }
    }
}
