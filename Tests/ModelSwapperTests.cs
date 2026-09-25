using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Xestrel.Detection;
using Xestrel.Isolation;
using Xestrel.Runtime;

namespace Xestrel.Tests
{
    // Prefabs stand in for FBX models, as in ModelSwapValidatorTests.
    public class ModelSwapperTests
    {
        private const string TestDir = "Assets/XestrelTests";

        private GameObject _oldModel;
        private GameObject _newModel;
        private GameObject _variant;
        private GameObject _instance;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestDir))
                AssetDatabase.CreateFolder("Assets", "XestrelTests");

            _oldModel = CreateModel("OldModel");
            _newModel = CreateModel("NewModel");

            // Variant of the old model with an addition of its own, like a vendor's avatar prefab.
            var tmp = (GameObject)PrefabUtility.InstantiatePrefab(_oldModel);
            new GameObject("VariantAdd").transform.SetParent(tmp.transform.Find("Armature/Hips"));
            _variant = PrefabUtility.SaveAsPrefabAsset(tmp, TestDir + "/Variant.prefab");
            Object.DestroyImmediate(tmp);

            _instance = (GameObject)PrefabUtility.InstantiatePrefab(_variant);
            _instance.name = "TestAvatar";
            new GameObject("SceneAdd").transform.SetParent(_instance.transform.Find("Armature/Hips"));
        }

        [TearDown]
        public void TearDown()
        {
            if (_instance != null) Object.DestroyImmediate(_instance);
            AssetDatabase.DeleteAsset(TestDir);
            AssetDatabase.DeleteAsset("Assets/Xestrel");
            AssetDatabase.SaveAssets();
        }

        private static GameObject CreateModel(string name)
        {
            var root = new GameObject(name);
            var armature = new GameObject("Armature").transform;
            armature.SetParent(root.transform);
            new GameObject("Hips").transform.SetParent(armature);
            var path = $"{TestDir}/{name}.prefab";
            var asset = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);

            // A sub-asset with the same name in every model, like an FBX's mesh.
            var mesh = new Mesh { name = "BodyMesh" };
            AssetDatabase.AddObjectToAsset(mesh, path);
            AssetDatabase.SaveAssets();
            return asset;
        }

        [Test]
        public void Swap_RebasesACopyAndKeepsAdditions()
        {
            Assert.That(ModelSwapper.CannotSwapReason(_instance, _newModel), Is.Null);

            Assert.That(ModelSwapper.Swap(_instance, _newModel), Is.True);

            Assert.That(ModelSwapValidator.FindBaseModel(_instance), Is.SameAs(_newModel));
            var source = PrefabUtility.GetCorrespondingObjectFromSource(_instance);
            Assert.That(AssetDatabase.GetAssetPath(source), Does.StartWith(IsolationPaths.Root + "/"));
            Assert.That(_instance.transform.Find("Armature/Hips/VariantAdd"), Is.Not.Null, "variant addition carried over");
            Assert.That(_instance.transform.Find("Armature/Hips/SceneAdd"), Is.Not.Null, "scene addition carried over");

            Assert.That(PrefabUtility.GetCorrespondingObjectFromOriginalSource(_variant), Is.SameAs(_oldModel),
                "the original variant must stay based on the old model");

            var state = _instance.GetComponent<XestrelMaterialIsolation>();
            var swap = ModelSwapper.CurrentSwap(state);
            Assert.That(swap, Is.Not.Null);
            Assert.That(swap.originalPrefab, Is.SameAs(_variant));
            Assert.That(swap.originalModel, Is.SameAs(_oldModel));
            Assert.That(swap.prefabCopies.Count, Is.EqualTo(1));
            Assert.That(ModelSwapper.CannotSwapReason(_instance, _oldModel), Is.Not.Null, "already swapped");
        }

        [Test]
        public void Restore_PointsBackAtTheOriginalVariant()
        {
            ModelSwapper.Swap(_instance, _newModel);
            var state = _instance.GetComponent<XestrelMaterialIsolation>();

            Assert.That(ModelSwapper.Restore(state), Is.True);

            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(_instance), Is.SameAs(_variant));
            Assert.That(ModelSwapValidator.FindBaseModel(_instance), Is.SameAs(_oldModel));
            Assert.That(_instance.transform.Find("Armature/Hips/SceneAdd"), Is.Not.Null);
            Assert.That(ModelSwapper.CurrentSwap(_instance.GetComponent<XestrelMaterialIsolation>()), Is.Null);
        }

        [Test]
        public void IsolatorRestore_AlsoRestoresTheModel()
        {
            ModelSwapper.Swap(_instance, _newModel);

            Isolator.Restore(_instance.GetComponent<XestrelMaterialIsolation>());

            Assert.That(ModelSwapValidator.FindBaseModel(_instance), Is.SameAs(_oldModel));
        }

        [Test]
        public void CannotSwap_NonPrefabObject()
        {
            var plain = new GameObject("Plain");
            try
            {
                Assert.That(ModelSwapper.CannotSwapReason(plain, _newModel), Is.Not.Null);
                Assert.That(ModelSwapper.Swap(plain, _newModel), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(plain);
            }
        }

        [Test]
        public void RemapModelReferences_RepointsSameNamedSubAsset()
        {
            var oldMesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GetAssetPath(_oldModel));
            var newMesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GetAssetPath(_newModel));
            var go = new GameObject("Holder");
            try
            {
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = oldMesh;

                var n = ModelSwapper.RemapModelReferences(go, AssetDatabase.GetAssetPath(_oldModel), _newModel, false);

                Assert.That(n, Is.EqualTo(1));
                Assert.That(mf.sharedMesh, Is.SameAs(newMesh));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
