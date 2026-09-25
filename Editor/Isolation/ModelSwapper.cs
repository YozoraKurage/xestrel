using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Xestrel.Core;
using Xestrel.Detection;
using Xestrel.Runtime;

namespace Xestrel.Isolation
{
    /// <summary>
    /// Swaps the model (FBX) an avatar is built on for an "almost identical" one without
    /// touching either model or the avatar's original prefab variants:
    /// <list type="number">
    /// <item>every prefab variant between the scene instance and the old model is rebuilt,
    ///   innermost first, as a variant of the new model / the next rebuilt copy under
    ///   <c>Assets/Xestrel/&lt;AvatarName&gt;/Prefabs/</c> (see <see cref="BuildRebasedVariant"/>);</item>
    /// <item>the scene instance is pointed at the outermost copy the same way.</item>
    /// </list>
    /// Overrides that referenced the old model's sub-assets (mesh, Avatar …) directly are
    /// remapped to the same-named sub-asset of the new model. Restore reverses the scene
    /// instance; the copies stay on disk. Run <see cref="ModelSwapValidator"/> first to
    /// see what will not carry over.
    /// </summary>
    internal static class ModelSwapper
    {
        private static readonly HashSet<string> SkippedProperties = new HashSet<string>
        {
            "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject",
        };

        private static PrefabReplacingSettings Settings => new PrefabReplacingSettings
        {
            logInfo = false,
            objectMatchMode = ObjectMatchMode.ByHierarchy,
            prefabOverridesOptions = PrefabOverridesOptions.KeepAllPossibleOverrides,
            changeRootNameToAssetName = false,
        };

        public static XestrelModelSwapBinding CurrentSwap(XestrelMaterialIsolation state) =>
            state != null && state.modelSwaps != null && state.modelSwaps.Count > 0 ? state.modelSwaps[0] : null;

        /// <summary>Why the avatar cannot be swapped to <paramref name="newModel"/>, or null when it can.</summary>
        public static string CannotSwapReason(GameObject avatarRoot, GameObject newModel)
        {
            if (avatarRoot == null) return "アバターが指定されていません。";
            if (EditorUtility.IsPersistent(avatarRoot)) return "シーン上のアバターを指定してください（Prefab アセットは対象外です）。";
            if (!PrefabUtility.IsOutermostPrefabInstanceRoot(avatarRoot) || PrefabUtility.IsPrefabAssetMissing(avatarRoot))
                return "アバターが Prefab インスタンスではないため差し替えできません（Unpack 済み？）。";
            if (CurrentSwap(avatarRoot.GetComponent<XestrelMaterialIsolation>()) != null)
                return "すでに FBX を差し替えています。別の FBX にするには、先に元に戻してください。";
            if (newModel == null || !EditorUtility.IsPersistent(newModel))
                return "新しい FBX を指定してください。";
            var chain = SourceChain(avatarRoot);
            if (chain.Count == 0) return "元の FBX が見つかりません。";
            if (chain[chain.Count - 1] == newModel) return "元の FBX と新しい FBX が同じです。";
            if (chain.Contains(newModel)) return "新しい FBX がアバターの Prefab 構成にすでに含まれています。";
            return null;
        }

        /// <summary>
        /// The prefab assets behind the scene instance, outermost first: the variants,
        /// then the base model last.
        /// </summary>
        internal static List<GameObject> SourceChain(GameObject avatarRoot)
        {
            var chain = new List<GameObject>();
            var cur = PrefabUtility.GetCorrespondingObjectFromSource(avatarRoot);
            while (cur != null && !chain.Contains(cur))
            {
                chain.Add(cur);
                cur = PrefabUtility.GetCorrespondingObjectFromSource(cur);
            }
            return chain;
        }

        public static bool Swap(GameObject avatarRoot, GameObject newModel)
        {
            var reason = CannotSwapReason(avatarRoot, newModel);
            if (reason != null)
            {
                XestrelLog.Warn(XestrelLogCategory.Isolate, "Model swap refused: " + reason);
                return false;
            }

            var chain = SourceChain(avatarRoot);
            var originalPrefab = chain[0];
            var oldModel = chain[chain.Count - 1];
            var variants = chain.GetRange(0, chain.Count - 1);

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();

            var state = avatarRoot.GetComponent<XestrelMaterialIsolation>();
            if (state == null) state = Undo.AddComponent<XestrelMaterialIsolation>(avatarRoot);
            MaterialIsolator.EnsureWorkspaceName(state, avatarRoot);

            var copies = new List<GameObject>();
            try
            {
                var dir = IsolationPaths.PrefabsDir(state.avatarName);
                if (variants.Count > 0 && XestrelPaths.EnsureDirectory(dir) == null)
                    throw new System.Exception("could not create " + dir);

                // Innermost first: each variant is rebuilt on top of the previous result.
                var oldModelPath = AssetDatabase.GetAssetPath(oldModel);
                var newBase = newModel;
                for (int i = variants.Count - 1; i >= 0; i--)
                {
                    var dst = AssetDatabase.GenerateUniqueAssetPath(
                        dir + "/" + XestrelPaths.SanitiseFileSegment(variants[i].name) + "__xestrel.prefab");
                    newBase = BuildRebasedVariant(variants[i], newBase, dst, oldModelPath, newModel, avatarRoot.scene);
                    copies.Insert(0, newBase);
                }
            }
            catch (System.Exception ex)
            {
                foreach (var c in copies)
                    if (c != null) AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(c));
                XestrelLog.Error(XestrelLogCategory.Isolate, $"Model swap failed on '{avatarRoot.name}': {ex.Message}");
                Undo.RevertAllDownToGroup(undoGroup);
                return false;
            }

            var target = copies.Count > 0 ? copies[0] : newModel;
            PrefabUtility.ReplacePrefabAssetOfPrefabInstance(avatarRoot, target, Settings, InteractionMode.UserAction);
            var remapped = RemapModelReferences(avatarRoot, AssetDatabase.GetAssetPath(oldModel), newModel, true);

            state = avatarRoot.GetComponent<XestrelMaterialIsolation>();
            Undo.RecordObject(state, "Xestrel Swap Model");
            if (state.modelSwaps == null) state.modelSwaps = new List<XestrelModelSwapBinding>();
            state.modelSwaps.Clear();
            state.modelSwaps.Add(new XestrelModelSwapBinding
            {
                originalPrefab = originalPrefab,
                swappedPrefab = target,
                originalModel = oldModel,
                newModel = newModel,
                prefabCopies = copies,
            });
            EditorUtility.SetDirty(state);
            WorkspaceManifests.Sync(state);
            AssetDatabase.SaveAssets();

            Undo.SetCurrentGroupName("Xestrel Swap Model");
            Undo.CollapseUndoOperations(undoGroup);
            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Swapped model of '{avatarRoot.name}': {oldModel.name} → {newModel.name} " +
                $"({copies.Count} prefab copy(ies), {remapped} scene reference(s) remapped)");
            return true;
        }

        /// <summary>
        /// Point the scene instance back at the prefab it used before the swap and drop
        /// the record. The prefab copies stay on disk. Returns false when not swapped.
        /// </summary>
        public static bool Restore(XestrelMaterialIsolation state)
        {
            var swap = CurrentSwap(state);
            if (swap == null) return false;
            var avatarRoot = state.gameObject;

            if (swap.originalPrefab == null)
            {
                XestrelLog.Error(XestrelLogCategory.Isolate,
                    $"Cannot restore model of '{avatarRoot.name}': the original prefab asset is missing");
                return false;
            }

            if (PrefabUtility.GetCorrespondingObjectFromSource(avatarRoot) != swap.originalPrefab)
            {
                PrefabUtility.ReplacePrefabAssetOfPrefabInstance(
                    avatarRoot, swap.originalPrefab, Settings, InteractionMode.UserAction);
            }
            if (swap.newModel != null && swap.originalModel != null)
            {
                RemapModelReferences(avatarRoot, AssetDatabase.GetAssetPath(swap.newModel), swap.originalModel, true);
            }

            state = avatarRoot.GetComponent<XestrelMaterialIsolation>();
            Undo.RecordObject(state, "Xestrel Restore Model");
            state.modelSwaps.Clear();
            EditorUtility.SetDirty(state);
            WorkspaceManifests.Sync(state);
            AssetDatabase.SaveAssets();
            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Restored model of '{avatarRoot.name}' to '{swap.originalPrefab.name}' (prefab copies left on disk)");
            return true;
        }

        /// <summary>
        /// Save a new variant of <paramref name="newBase"/> at <paramref name="dstPath"/>
        /// carrying <paramref name="variant"/>'s overrides and additions. Unity refuses to
        /// replace the parent of a variant asset in place, so instead: instantiate the
        /// variant temporarily in <paramref name="scene"/>, unpack its outermost root
        /// (leaving an instance of its base with the variant's changes as instance
        /// overrides), replace that instance's prefab asset, and save the result as a prefab.
        /// The scene must be a regular scene: in a preview scene Unity treats the root as
        /// a variant being edited and refuses the replace just the same. Opening an extra
        /// additive scene is not an option either (Unity refuses while an untitled scene
        /// is unsaved), so the avatar's own scene hosts the temporary instance.
        /// </summary>
        private static GameObject BuildRebasedVariant(GameObject variant, GameObject newBase, string dstPath,
            string oldModelPath, GameObject newModel, Scene scene)
        {
            GameObject instance = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(variant, scene);
                if (instance == null) throw new System.Exception($"could not instantiate {variant.name}");
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
                PrefabUtility.ReplacePrefabAssetOfPrefabInstance(instance, newBase, Settings, InteractionMode.AutomatedAction);
                RemapModelReferences(instance, oldModelPath, newModel, false);
                var saved = PrefabUtility.SaveAsPrefabAsset(instance, dstPath, out var success);
                if (!success || saved == null) throw new System.Exception($"could not save {dstPath}");
                return saved;
            }
            finally
            {
                if (instance != null) Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Repoint every reference under <paramref name="root"/> to a sub-asset of the model
        /// at <paramref name="fromModelPath"/> at the sub-asset of <paramref name="toModel"/>
        /// with the same type and name. References inherited from the model are already
        /// re-resolved by the swap, so in practice this only rewrites overrides and
        /// additions. Returns the number of references rewritten.
        /// </summary>
        internal static int RemapModelReferences(GameObject root, string fromModelPath, GameObject toModel, bool undo)
        {
            if (root == null || string.IsNullOrEmpty(fromModelPath) || toModel == null) return 0;
            var toPath = AssetDatabase.GetAssetPath(toModel);
            var targets = new Dictionary<string, Object>();
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(toPath))
            {
                if (a == null) continue;
                var key = a.GetType().FullName + "|" + a.name;
                if (!targets.ContainsKey(key)) targets[key] = a;
            }

            int rewritten = 0;
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform || c is XestrelMaterialIsolation) continue;
                var so = new SerializedObject(c);
                var prop = so.GetIterator();
                bool enterChildren = true;
                bool changed = false;
                while (prop.Next(enterChildren))
                {
                    enterChildren = prop.propertyType != SerializedPropertyType.String;
                    if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (SkippedProperties.Contains(prop.name)) continue;
                    var value = prop.objectReferenceValue;
                    if (value == null || AssetDatabase.GetAssetPath(value) != fromModelPath) continue;

                    if (targets.TryGetValue(value.GetType().FullName + "|" + value.name, out var replacement))
                    {
                        prop.objectReferenceValue = replacement;
                        changed = true;
                        rewritten++;
                    }
                    else
                    {
                        XestrelLog.Warn(XestrelLogCategory.Isolate,
                            $"{c.GetType().Name} on '{c.gameObject.name}' keeps referencing '{value.name}' " +
                            "of the previous model: no sub-asset with that name in the other model");
                    }
                }
                if (!changed) continue;
                if (undo) so.ApplyModifiedProperties();
                else so.ApplyModifiedPropertiesWithoutUndo();
            }
            return rewritten;
        }
    }
}
