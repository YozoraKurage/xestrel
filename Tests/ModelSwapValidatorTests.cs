using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Xestrel.Detection;

namespace Xestrel.Tests
{
    // Prefabs stand in for FBX models: the validator matches objects by hierarchy path,
    // which is the same for both kinds of asset.
    public class ModelSwapValidatorTests
    {
        private const string TestDir = "Assets/XestrelTests";

        private GameObject _instance;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestDir))
                AssetDatabase.CreateFolder("Assets", "XestrelTests");
        }

        [TearDown]
        public void TearDown()
        {
            if (_instance != null) Object.DestroyImmediate(_instance);
            AssetDatabase.DeleteAsset(TestDir);
            AssetDatabase.SaveAssets();
        }

        private static GameObject CreateModel(string name, Mesh bodyMesh, params string[] paths)
        {
            var root = new GameObject(name);
            foreach (var path in paths)
            {
                var parent = root.transform;
                foreach (var part in path.Split('/'))
                {
                    var child = parent.Find(part);
                    if (child == null)
                    {
                        child = new GameObject(part).transform;
                        child.SetParent(parent);
                    }
                    parent = child;
                }
            }
            if (bodyMesh != null)
            {
                var body = new GameObject("Body");
                body.transform.SetParent(root.transform);
                body.AddComponent<SkinnedMeshRenderer>().sharedMesh = bodyMesh;
            }
            var asset = PrefabUtility.SaveAsPrefabAsset(root, $"{TestDir}/{name}.prefab");
            Object.DestroyImmediate(root);
            return asset;
        }

        private static Mesh CreateMesh(string name, params string[] blendShapes)
        {
            var mesh = new Mesh
            {
                name = name,
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 },
            };
            var deltas = new Vector3[3];
            foreach (var shape in blendShapes) mesh.AddBlendShapeFrame(shape, 100f, deltas, null, null);
            AssetDatabase.CreateAsset(mesh, $"{TestDir}/{name}.asset");
            return mesh;
        }

        [Test]
        public void IdenticalModels_ReportNoWarnings()
        {
            var oldModel = CreateModel("Old", null, "Armature/Hips/Spine");
            var newModel = CreateModel("New", null, "Armature/Hips/Spine");

            var report = ModelSwapValidator.Validate(oldModel, newModel, null);

            Assert.That(report.Count(SwapSeverity.Error), Is.EqualTo(0));
            Assert.That(report.Count(SwapSeverity.Warning), Is.EqualTo(0));
            Assert.That(report.matchedObjectCount, Is.EqualTo(report.oldObjectCount));
        }

        [Test]
        public void MissingSubtree_IsReportedOnceAtItsTop()
        {
            var oldModel = CreateModel("Old", null, "Armature/Hips/Tail/TailEnd");
            var newModel = CreateModel("New", null, "Armature/Hips", "Armature/Hips/Extra");

            var report = ModelSwapValidator.Validate(oldModel, newModel, null);

            var missing = report.issues.FindAll(i => i.kind == SwapIssueKind.MissingObject);
            Assert.That(missing.Count, Is.EqualTo(1));
            Assert.That(missing[0].path, Is.EqualTo("Armature/Hips/Tail"));
            Assert.That(report.issues.Exists(i => i.kind == SwapIssueKind.NewObject && i.path == "Armature/Hips/Extra"));
        }

        [Test]
        public void MissingBlendShape_IsWarned()
        {
            var oldModel = CreateModel("Old", CreateMesh("OldMesh", "Smile", "Blink"), "Armature");
            var newModel = CreateModel("New", CreateMesh("NewMesh", "Smile"), "Armature");

            var report = ModelSwapValidator.Validate(oldModel, newModel, null);

            var issue = report.issues.Find(i => i.kind == SwapIssueKind.BlendShapeMissing);
            Assert.That(issue, Is.Not.Null);
            Assert.That(issue.path, Is.EqualTo("Body"));
            Assert.That(issue.message, Does.Contain("Blink"));
        }

        [Test]
        public void AvatarAdditionsUnderMissingObject_AreReportedAsLost()
        {
            var oldModel = CreateModel("Old", null, "Armature/Hips/Tail");
            var newModel = CreateModel("New", null, "Armature/Hips");

            _instance = (GameObject)PrefabUtility.InstantiatePrefab(oldModel);
            Assert.That(ModelSwapValidator.FindBaseModel(_instance), Is.SameAs(oldModel));

            var hips = _instance.transform.Find("Armature/Hips");
            var tail = hips.Find("Tail");
            new GameObject("Accessory").transform.SetParent(tail);
            var tailBody = tail.gameObject.AddComponent<Rigidbody>();
            hips.gameObject.AddComponent<FixedJoint>().connectedBody = tailBody;

            var report = ModelSwapValidator.Validate(oldModel, newModel, _instance);

            Assert.That(report.issues.Exists(i =>
                i.kind == SwapIssueKind.LostAddedObject && i.path == "Armature/Hips/Tail/Accessory"));
            Assert.That(report.issues.Exists(i =>
                i.kind == SwapIssueKind.LostAddedComponent && i.path == "Armature/Hips/Tail"));
            Assert.That(report.issues.Exists(i =>
                i.kind == SwapIssueKind.BrokenReference && i.path == "Armature/Hips"));
        }

        [Test]
        public void UnchangedInstance_HasNoAvatarImpact()
        {
            var oldModel = CreateModel("Old", null, "Armature/Hips/Spine");
            var newModel = CreateModel("New", null, "Armature/Hips/Spine");
            _instance = (GameObject)PrefabUtility.InstantiatePrefab(oldModel);

            var report = ModelSwapValidator.Validate(oldModel, newModel, _instance);

            Assert.That(report.Count(SwapSeverity.Error), Is.EqualTo(0));
            Assert.That(report.Count(SwapSeverity.Warning), Is.EqualTo(0));
        }
    }
}
