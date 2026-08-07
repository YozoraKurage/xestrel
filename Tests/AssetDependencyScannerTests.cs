using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Xestrel.Detection;
using Xestrel.Isolation;
using Xestrel.Runtime;

namespace Xestrel.Tests
{
    public class AssetDependencyScannerTests
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

        private static SceneAssetRef FindRef(System.Collections.Generic.List<SceneAssetRef> refs, Object asset)
        {
            foreach (var r in refs)
            {
                if (r.asset == asset) return r;
            }
            return null;
        }

        [Test]
        public void CollectSceneRefs_FindsRendererMaterialWithUsage()
        {
            var refs = AssetDependencyScanner.CollectSceneRefs(_avatar);

            var entry = FindRef(refs, _sourceMat);
            Assert.That(entry, Is.Not.Null, "the renderer's material must be discovered generically");
            Assert.That(entry.usedBy, Has.Member("MeshRenderer \"Mesh\""));
        }

        [Test]
        public void CollectSceneRefs_SkipsXestrelBookkeeping()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            var copy = state.bindings[0].copy;

            var refs = AssetDependencyScanner.CollectSceneRefs(_avatar);

            Assert.That(FindRef(refs, copy), Is.Not.Null,
                "the renderer now references the copy");
            Assert.That(FindRef(refs, _sourceMat), Is.Null,
                "the original is only referenced by xestrel's bindings, which are bookkeeping");
            Assert.That(FindRef(refs, state.workspaceManifest), Is.Null,
                "the manifest reference is bookkeeping too");
        }

        [Test]
        public void CollectAssetRefs_MaterialReferencesItsTexture()
        {
            var deps = AssetDependencyScanner.CollectAssetRefs(_sourceMat);

            Assert.That(deps, Has.Member(_sourceTex));
        }

        [Test]
        public void KindOf_ClassifiesCommonAssets()
        {
            Assert.That(AssetDependencyScanner.KindOf(_sourceMat), Is.EqualTo(AssetKind.Material));
            Assert.That(AssetDependencyScanner.KindOf(_sourceTex), Is.EqualTo(AssetKind.Texture));
            var clip = new AnimationClip();
            Assert.That(AssetDependencyScanner.KindOf(clip), Is.EqualTo(AssetKind.Clip));
            Object.DestroyImmediate(clip);
        }
    }
}
