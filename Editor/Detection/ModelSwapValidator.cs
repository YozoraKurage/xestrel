using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Xestrel.Runtime;

namespace Xestrel.Detection
{
    internal enum SwapSeverity { Info, Warning, Error }

    internal enum SwapIssueKind
    {
        // Model ↔ model structure
        MissingObject,       // path exists in the old model only
        NewObject,           // path exists in the new model only
        AmbiguousName,       // siblings share a name, so path matching is ambiguous
        ComponentMissing,    // matched object lost a component type (e.g. SkinnedMeshRenderer)
        BlendShapeMissing,
        BlendShapeAdded,
        SubMeshCountChanged,
        MeshStatsChanged,    // vertex / bone count — expected when the mesh was edited
        Humanoid,
        // Impact on the avatar built on top of the old model
        LostAddedObject,     // an object added on the avatar hangs under a missing object
        LostAddedComponent,  // a component added on the avatar sits on a missing object
        BrokenReference,     // something on the avatar references a missing object
        StaleOldReference,   // an override / addition keeps pointing at the old model's sub-assets
    }

    internal sealed class SwapIssue
    {
        public SwapSeverity severity;
        public SwapIssueKind kind;
        public string path;     // hierarchy path the issue is about ("" = root)
        public string message;
        public Object context;  // scene object to ping, when there is one
    }

    internal sealed class ModelSwapReport
    {
        public GameObject oldModel;
        public GameObject newModel;
        public GameObject avatarRoot;
        public int oldObjectCount;
        public int newObjectCount;
        public int matchedObjectCount;
        public readonly List<SwapIssue> issues = new List<SwapIssue>();

        public int Count(SwapSeverity s) => issues.Count(i => i.severity == s);
        public bool HasKind(SwapIssueKind k) => issues.Any(i => i.kind == k);
    }

    /// <summary>
    /// Read-only pre-flight check for swapping the model (FBX) an avatar is built on
    /// for an "almost identical" one. Unity matches the objects of a model by name and
    /// hierarchy, so this compares both models by hierarchy path and reports what would
    /// not carry over: objects that disappear, mesh / blend shape / humanoid
    /// differences, and — given the avatar — additions, overrides and references that
    /// would be lost or keep pointing at the old model. Nothing is modified.
    /// </summary>
    internal static class ModelSwapValidator
    {
        private const int MaxNamesListed = 8;

        private static readonly HashSet<string> SkippedProperties = new HashSet<string>
        {
            "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject",
        };

        /// <summary>
        /// The model asset the avatar is ultimately built on (the original source of its
        /// root, through any prefab variants), or null when the avatar is not a prefab instance.
        /// </summary>
        public static GameObject FindBaseModel(GameObject avatarRoot)
        {
            if (avatarRoot == null) return null;
            var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(avatarRoot);
            if (src == null) return null;
            var path = AssetDatabase.GetAssetPath(src);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
        }

        public static bool IsModelAsset(GameObject asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            return !string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is ModelImporter;
        }

        public static ModelSwapReport Validate(GameObject oldModel, GameObject newModel, GameObject avatarRoot)
        {
            var report = new ModelSwapReport { oldModel = oldModel, newModel = newModel, avatarRoot = avatarRoot };
            if (oldModel == null || newModel == null) return report;

            var oldPaths = CollectPaths(oldModel.transform, out var oldDup);
            var newPaths = CollectPaths(newModel.transform, out var newDup);
            report.oldObjectCount = oldPaths.Count;
            report.newObjectCount = newPaths.Count;

            CompareHierarchy(report, oldPaths, newPaths, oldDup, newDup);
            foreach (var kv in oldPaths)
            {
                if (!newPaths.TryGetValue(kv.Key, out var nt)) continue;
                report.matchedObjectCount++;
                CompareComponents(report, kv.Key, kv.Value.gameObject, nt.gameObject);
                CompareMeshes(report, kv.Key, MeshOf(kv.Value.gameObject), MeshOf(nt.gameObject));
            }
            CompareHumanoid(report, oldModel, newModel);

            if (avatarRoot != null)
                AnalyseAvatar(report, AssetDatabase.GetAssetPath(oldModel), newPaths);

            report.issues.Sort((a, b) =>
            {
                var s = b.severity.CompareTo(a.severity);
                if (s != 0) return s;
                var k = a.kind.CompareTo(b.kind);
                return k != 0 ? k : string.CompareOrdinal(a.path, b.path);
            });
            return report;
        }

        // ---------- model ↔ model ----------

        private static void CompareHierarchy(ModelSwapReport report,
            Dictionary<string, Transform> oldPaths, Dictionary<string, Transform> newPaths,
            HashSet<string> oldDup, HashSet<string> newDup)
        {
            foreach (var path in oldPaths.Keys)
            {
                if (newPaths.ContainsKey(path)) continue;
                // Report only the topmost missing object; its subtree goes with it.
                if (path.Length > 0 && !newPaths.ContainsKey(ParentPath(path)) && oldPaths.ContainsKey(ParentPath(path)))
                    continue;
                var descendants = oldPaths.Keys.Count(p => p.StartsWith(path + "/", System.StringComparison.Ordinal));
                Add(report, SwapSeverity.Warning, SwapIssueKind.MissingObject, path,
                    descendants > 0
                        ? $"新しい FBX にありません（子孫 {descendants} 個も含む）"
                        : "新しい FBX にありません");
            }
            foreach (var path in newPaths.Keys)
            {
                if (oldPaths.ContainsKey(path)) continue;
                if (path.Length > 0 && !oldPaths.ContainsKey(ParentPath(path)) && newPaths.ContainsKey(ParentPath(path)))
                    continue;
                Add(report, SwapSeverity.Info, SwapIssueKind.NewObject, path, "新しい FBX にだけあります");
            }
            foreach (var path in oldDup.Union(newDup))
            {
                Add(report, SwapSeverity.Warning, SwapIssueKind.AmbiguousName, path,
                    "同じ名前の兄弟オブジェクトがあり、どれとどれが対応するか曖昧です");
            }
        }

        private static void CompareComponents(ModelSwapReport report, string path, GameObject oldGo, GameObject newGo)
        {
            var newTypes = new HashSet<System.Type>(newGo.GetComponents<Component>()
                .Where(c => c != null).Select(c => c.GetType()));
            foreach (var c in oldGo.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                if (newTypes.Contains(c.GetType())) continue;
                Add(report, SwapSeverity.Warning, SwapIssueKind.ComponentMissing, path,
                    $"{c.GetType().Name} が新しい FBX 側にありません");
            }
        }

        private static void CompareMeshes(ModelSwapReport report, string path, Mesh oldMesh, Mesh newMesh)
        {
            if (oldMesh == null || newMesh == null) return;

            var oldShapes = BlendShapeNames(oldMesh);
            var newShapes = BlendShapeNames(newMesh);
            var missing = oldShapes.Where(s => !newShapes.Contains(s)).ToList();
            var added = newShapes.Where(s => !oldShapes.Contains(s)).ToList();
            if (missing.Count > 0)
            {
                Add(report, SwapSeverity.Warning, SwapIssueKind.BlendShapeMissing, path,
                    $"BlendShape が {missing.Count} 個なくなります: {JoinNames(missing)}" +
                    "（このシェイプキーを使うアニメーション・表情・リップシンクが効かなくなります）");
            }
            if (added.Count > 0)
            {
                Add(report, SwapSeverity.Info, SwapIssueKind.BlendShapeAdded, path,
                    $"BlendShape が {added.Count} 個増えます: {JoinNames(added)}");
            }

            if (oldMesh.subMeshCount != newMesh.subMeshCount)
            {
                Add(report,
                    newMesh.subMeshCount < oldMesh.subMeshCount ? SwapSeverity.Warning : SwapSeverity.Info,
                    SwapIssueKind.SubMeshCountChanged, path,
                    $"マテリアルスロット数が {oldMesh.subMeshCount} → {newMesh.subMeshCount} に変わります" +
                    "（スロットごとのマテリアル割り当てがずれる可能性があります）");
            }

            var stats = new List<string>();
            if (oldMesh.vertexCount != newMesh.vertexCount)
                stats.Add($"頂点数 {oldMesh.vertexCount} → {newMesh.vertexCount}");
            if (oldMesh.bindposes.Length != newMesh.bindposes.Length)
                stats.Add($"ボーン数 {oldMesh.bindposes.Length} → {newMesh.bindposes.Length}");
            if (stats.Count > 0)
            {
                Add(report, SwapSeverity.Info, SwapIssueKind.MeshStatsChanged, path,
                    "メッシュが変わっています: " + string.Join(" / ", stats));
            }
        }

        private static void CompareHumanoid(ModelSwapReport report, GameObject oldModel, GameObject newModel)
        {
            var oi = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(oldModel)) as ModelImporter;
            var ni = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(newModel)) as ModelImporter;
            if (oi == null || ni == null) return;

            var oldHuman = oi.animationType == ModelImporterAnimationType.Human;
            var newHuman = ni.animationType == ModelImporterAnimationType.Human;
            if (oldHuman != newHuman)
            {
                Add(report, SwapSeverity.Warning, SwapIssueKind.Humanoid, "",
                    $"Rig の種類が違います（元: {oi.animationType} / 新: {ni.animationType}）");
                return;
            }
            if (!oldHuman) return;

            var oldMap = HumanMap(oi);
            var newMap = HumanMap(ni);
            var missing = new List<string>();
            var changed = new List<string>();
            foreach (var kv in oldMap)
            {
                if (!newMap.TryGetValue(kv.Key, out var bone)) missing.Add(kv.Key);
                else if (bone != kv.Value) changed.Add($"{kv.Key}: {kv.Value} → {bone}");
            }
            if (missing.Count > 0)
            {
                Add(report, SwapSeverity.Warning, SwapIssueKind.Humanoid, "",
                    $"Humanoid のボーン割り当てが {missing.Count} 個なくなります: {JoinNames(missing)}");
            }
            if (changed.Count > 0)
            {
                Add(report, SwapSeverity.Warning, SwapIssueKind.Humanoid, "",
                    $"Humanoid のボーン割り当てが {changed.Count} 個変わります: {JoinNames(changed)}");
            }
        }

        private static Dictionary<string, string> HumanMap(ModelImporter importer)
        {
            var map = new Dictionary<string, string>();
            var human = importer.humanDescription.human;
            if (human == null) return map;
            foreach (var hb in human)
            {
                if (!string.IsNullOrEmpty(hb.humanName)) map[hb.humanName] = hb.boneName;
            }
            return map;
        }

        // ---------- impact on the avatar ----------

        private static void AnalyseAvatar(ModelSwapReport report, string oldModelPath,
            Dictionary<string, Transform> newPaths)
        {
            var avatarRoot = report.avatarRoot;
            // For every object on the avatar: its hierarchy path inside the old model,
            // or null when the object does not come from the old model (added on the
            // avatar / variant, or part of another nested prefab).
            var originPath = new Dictionary<GameObject, string>();
            foreach (var t in avatarRoot.GetComponentsInChildren<Transform>(true))
            {
                originPath[t.gameObject] = OriginPathIn(t.gameObject, oldModelPath);
            }

            bool IsMissing(GameObject go) =>
                go != null && originPath.TryGetValue(go, out var p) && p != null && !newPaths.ContainsKey(p);

            foreach (var kv in originPath)
            {
                var go = kv.Key;
                if (kv.Value != null)
                {
                    if (!IsMissing(go)) continue;
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c == null || c is Transform || c is XestrelMaterialIsolation) continue;
                        if (OriginPathIn(c, oldModelPath) != null) continue;
                        Add(report, SwapSeverity.Error, SwapIssueKind.LostAddedComponent,
                            HierarchyPath(go.transform, avatarRoot.transform),
                            $"追加した {c.GetType().Name} が付いているオブジェクトが新しい FBX にありません", c);
                    }
                }
                else
                {
                    var parent = go.transform.parent;
                    if (parent == null || go == avatarRoot) continue;
                    if (!IsMissing(parent.gameObject)) continue;
                    Add(report, SwapSeverity.Error, SwapIssueKind.LostAddedObject,
                        HierarchyPath(go.transform, avatarRoot.transform),
                        $"追加したオブジェクトの親「{parent.name}」が新しい FBX にありません", go);
                }
            }

            foreach (var c in avatarRoot.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform || c is XestrelMaterialIsolation) continue;
                if (IsMissing(c.gameObject)) continue; // reported above, or goes away with the model

                var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(c);
                var fromOldModel = source != null && AssetDatabase.GetAssetPath(source) == oldModelPath;
                var sourceSo = fromOldModel ? new SerializedObject(source) : null;

                var so = new SerializedObject(c);
                var prop = so.GetIterator();
                bool enterChildren = true;
                while (prop.Next(enterChildren))
                {
                    enterChildren = prop.propertyType != SerializedPropertyType.String;
                    if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (SkippedProperties.Contains(prop.name)) continue;
                    var value = prop.objectReferenceValue;
                    if (value == null) continue;

                    // Values inherited unchanged from the old model are re-resolved against
                    // the new model by the swap itself; only overrides and additions stick.
                    if (sourceSo != null && InheritedFromSource(value, sourceSo.FindProperty(prop.propertyPath)))
                        continue;

                    var assetPath = AssetDatabase.GetAssetPath(value);
                    var where = HierarchyPath(c.transform, avatarRoot.transform);
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        var target = value as GameObject ?? (value as Component)?.gameObject;
                        if (!IsMissing(target)) continue;
                        Add(report, SwapSeverity.Error, SwapIssueKind.BrokenReference, where,
                            $"{c.GetType().Name} の {prop.displayName} が「{target.name}」を参照していますが、" +
                            "新しい FBX にないため参照が外れます", c);
                    }
                    else if (assetPath == oldModelPath)
                    {
                        Add(report, SwapSeverity.Warning, SwapIssueKind.StaleOldReference, where,
                            $"{c.GetType().Name} の {prop.displayName} が元の FBX の「{value.name}」を直接参照しています" +
                            "（差し替え後も元の FBX を使い続けます）", c);
                    }
                }
            }
        }

        private static bool InheritedFromSource(Object value, SerializedProperty sourceProp)
        {
            if (sourceProp == null || sourceProp.propertyType != SerializedPropertyType.ObjectReference) return false;
            var sourceValue = sourceProp.objectReferenceValue;
            if (value == sourceValue) return true;
            // Scene objects inside the instance correspond to the model's own objects.
            return sourceValue != null && PrefabUtility.GetCorrespondingObjectFromOriginalSource(value) == sourceValue;
        }

        /// <summary>Path of <paramref name="obj"/> inside the model at <paramref name="modelPath"/>, or null.</summary>
        private static string OriginPathIn(Object obj, string modelPath)
        {
            var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(obj);
            if (src == null || AssetDatabase.GetAssetPath(src) != modelPath) return null;
            var t = src is GameObject g ? g.transform : (src as Component)?.transform;
            return t != null ? HierarchyPath(t, t.root) : null;
        }

        // ---------- helpers ----------

        internal static Dictionary<string, Transform> CollectPaths(Transform root, out HashSet<string> duplicates)
        {
            var result = new Dictionary<string, Transform>();
            duplicates = new HashSet<string>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var path = HierarchyPath(t, root);
                if (result.ContainsKey(path)) duplicates.Add(path);
                else result[path] = t;
            }
            return result;
        }

        internal static string HierarchyPath(Transform t, Transform root)
        {
            if (t == root) return "";
            var parts = new List<string>();
            for (var cur = t; cur != null && cur != root; cur = cur.parent) parts.Add(cur.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string ParentPath(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash < 0 ? "" : path.Substring(0, slash);
        }

        private static Mesh MeshOf(GameObject go)
        {
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr != null) return smr.sharedMesh;
            var mf = go.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static List<string> BlendShapeNames(Mesh mesh)
        {
            var names = new List<string>(mesh.blendShapeCount);
            for (int i = 0; i < mesh.blendShapeCount; i++) names.Add(mesh.GetBlendShapeName(i));
            return names;
        }

        private static string JoinNames(List<string> names) =>
            names.Count <= MaxNamesListed
                ? string.Join(", ", names)
                : string.Join(", ", names.Take(MaxNamesListed)) + $" …ほか {names.Count - MaxNamesListed} 個";

        private static void Add(ModelSwapReport report, SwapSeverity severity, SwapIssueKind kind,
            string path, string message, Object context = null)
        {
            report.issues.Add(new SwapIssue
            {
                severity = severity, kind = kind, path = path, message = message, context = context,
            });
        }
    }
}
