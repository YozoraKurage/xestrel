using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Xestrel.Detection;
using Xestrel.Isolation;
using Xestrel.Runtime;

namespace Xestrel.Tests
{
    public class WorkspaceAuditTests
    {
        private const string TestSourceDir = "Assets/XestrelTests";
        private const string TestSourceMat = "Assets/XestrelTests/Source.mat";
        private const string TestSourceTex = "Assets/XestrelTests/Source.asset";

        private GameObject _avatar;
        private Material _sourceMat;
        private Texture2D _sourceTex;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestSourceDir))
                AssetDatabase.CreateFolder("Assets", "XestrelTests");

            _sourceTex = new Texture2D(4, 4) { name = "SourceTex" };
            AssetDatabase.CreateAsset(_sourceTex, TestSourceTex);

            _sourceMat = new Material(Shader.Find("Unlit/Texture")) { name = "SourceMat" };
            _sourceMat.mainTexture = _sourceTex;
            AssetDatabase.CreateAsset(_sourceMat, TestSourceMat);
            AssetDatabase.SaveAssets();

            _avatar = new GameObject("TestAvatar");
            var mfGo = new GameObject("Mesh");
            mfGo.transform.SetParent(_avatar.transform);
            var mr = mfGo.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new[] { _sourceMat };
        }

        [TearDown]
        public void TearDown()
        {
            if (_avatar != null) Object.DestroyImmediate(_avatar);
            AssetDatabase.DeleteAsset(TestSourceDir);
            AssetDatabase.DeleteAsset("Assets/Xestrel");
            AssetDatabase.SaveAssets();
        }

        [Test]
        public void CollectUnusedCopies_EmptyWhileEverythingIsBound()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            TextureIsolator.IsolateProperty(state, state.bindings[0].copy, "_MainTex");

            // The manifest and folders must not be flagged either.
            Assert.That(WorkspaceAudit.CollectUnusedCopies(state), Is.Empty);
        }

        [Test]
        public void CollectUnusedCopies_FlagsCopyLeftBehindByRestore()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            var copyMat = state.bindings[0].copy;

            MaterialIsolator.RestoreBinding(state, state.bindings[0]);

            var unused = WorkspaceAudit.CollectUnusedCopies(state);
            Assert.That(unused, Has.Member(copyMat),
                "the copy stays on disk after Restore but nothing references or tracks it");
        }

        [Test]
        public void CollectRendererMaterials_ReturnsDistinctMaterials()
        {
            var mr = _avatar.GetComponentInChildren<MeshRenderer>();
            mr.sharedMaterials = new[] { _sourceMat, _sourceMat };

            var mats = WorkspaceAudit.CollectRendererMaterials(_avatar);

            Assert.That(mats.Count, Is.EqualTo(1));
            Assert.That(mats[0], Is.SameAs(_sourceMat));
        }

        [Test]
        public void CollectMaterialTextures_ReturnsBoundTextures()
        {
            var textures = WorkspaceAudit.CollectMaterialTextures(_sourceMat);

            Assert.That(textures.Count, Is.EqualTo(1));
            Assert.That(textures[0], Is.SameAs(_sourceTex));
        }
    }

    public class AvatarAdditionsTests
    {
        private const string TestSourceDir = "Assets/XestrelTests";

        private GameObject _instance;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestSourceDir))
                AssetDatabase.CreateFolder("Assets", "XestrelTests");
        }

        [TearDown]
        public void TearDown()
        {
            if (_instance != null) Object.DestroyImmediate(_instance);
            AssetDatabase.DeleteAsset(TestSourceDir);
            AssetDatabase.DeleteAsset("Assets/Xestrel");
            AssetDatabase.SaveAssets();
        }

        private static GameObject MakePrefabAsset(string name, string path)
        {
            var go = new GameObject(name);
            var asset = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return asset;
        }

        [Test]
        public void Scan_ReportsAddedPrefabInstanceAndComponents()
        {
            var baseAsset = MakePrefabAsset("BaseAvatar", TestSourceDir + "/Base.prefab");
            _instance = (GameObject)PrefabUtility.InstantiatePrefab(baseAsset);

            var outfitAsset = MakePrefabAsset("Outfit", TestSourceDir + "/Outfit.prefab");
            var outfitInstance = (GameObject)PrefabUtility.InstantiatePrefab(outfitAsset);
            outfitInstance.transform.SetParent(_instance.transform);

            _instance.AddComponent<BoxCollider>();
            // xestrel's own component must not show up as a user addition.
            _instance.AddComponent<XestrelMaterialIsolation>();

            var report = AvatarAdditions.Scan(_instance);

            Assert.That(report.isPrefabInstance, Is.True);
            Assert.That(report.basePrefab, Is.SameAs(baseAsset));
            Assert.That(report.addedObjects, Has.Member(outfitInstance));
            Assert.That(report.addedComponents.Count, Is.EqualTo(1));
            Assert.That(report.addedComponents[0], Is.TypeOf<BoxCollider>());
        }

        [Test]
        public void Scan_FallsBackToChildPrefabInstancesForNonPrefabAvatar()
        {
            _instance = new GameObject("UnpackedAvatar");
            var plainChild = new GameObject("Body");
            plainChild.transform.SetParent(_instance.transform);

            var outfitAsset = MakePrefabAsset("Outfit", TestSourceDir + "/Outfit.prefab");
            var outfitInstance = (GameObject)PrefabUtility.InstantiatePrefab(outfitAsset);
            outfitInstance.transform.SetParent(_instance.transform);

            var report = AvatarAdditions.Scan(_instance);

            Assert.That(report.isPrefabInstance, Is.False);
            Assert.That(report.addedObjects, Has.Member(outfitInstance));
            Assert.That(report.addedObjects, Has.No.Member(plainChild),
                "scene-built children cannot be distinguished and are not listed");
        }
    }
}
