using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Xestrel.Core;
using Xestrel.Isolation;
using Xestrel.Runtime;

namespace Xestrel.Tests
{
    public class WorkspaceManifestTests
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
        public void Isolate_CreatesManifestMirroringTheComponent()
        {
            MaterialIsolator.Isolate(_avatar);

            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            var manifest = state.workspaceManifest as XestrelWorkspaceManifest;
            Assert.That(manifest, Is.Not.Null, "a workspace manifest should be created on first isolate");
            Assert.That(AssetDatabase.GetAssetPath(manifest),
                Is.EqualTo($"Assets/Xestrel/{state.avatarName}/{XestrelWorkspaceManifest.FileName}"));

            Assert.That(manifest.avatarName, Is.EqualTo(state.avatarName));
            Assert.That(manifest.bindings.Count, Is.EqualTo(1));
            Assert.That(manifest.bindings[0].original, Is.SameAs(_sourceMat));
            Assert.That(manifest.bindings[0].copy, Is.SameAs(state.bindings[0].copy));
            Assert.That(manifest.bindings[0], Is.Not.SameAs(state.bindings[0]),
                "the manifest must hold its own entries, not share instances with the component");

            Assert.That(manifest.guidHistory.Count, Is.EqualTo(1));
            Assert.That(manifest.guidHistory[0].kind, Is.EqualTo("material"));
        }

        [Test]
        public void Recover_RebuildsLostComponentFromManifest()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            TextureIsolator.IsolateProperty(state, state.bindings[0].copy, "_MainTex");
            var manifest = (XestrelWorkspaceManifest)state.workspaceManifest;
            var workspace = state.avatarName;
            var copyMat = state.bindings[0].copy;
            var copyTex = state.textureBindings[0].copy;

            Object.DestroyImmediate(state); // the component dies; renderers keep the copies

            var recovered = WorkspaceManifests.Recover(_avatar, manifest);

            Assert.That(recovered, Is.Not.Null);
            Assert.That(recovered.avatarName, Is.EqualTo(workspace));
            Assert.That(recovered.workspaceManifest, Is.SameAs(manifest));
            Assert.That(recovered.bindings.Count, Is.EqualTo(1));
            Assert.That(recovered.bindings[0].original, Is.SameAs(_sourceMat));
            Assert.That(recovered.bindings[0].copy, Is.SameAs(copyMat));
            Assert.That(recovered.textureBindings[0].copy, Is.SameAs(copyTex));

            // The recovered mapping is functional end-to-end.
            Isolator.Restore(recovered);
            Assert.That(_avatar.GetComponentInChildren<MeshRenderer>().sharedMaterials[0],
                Is.SameAs(_sourceMat));
        }

        [Test]
        public void Recover_RefusesToOverwriteComponentWithBindings()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            var manifest = (XestrelWorkspaceManifest)state.workspaceManifest;

            Assert.That(WorkspaceManifests.Recover(_avatar, manifest), Is.Null);
        }

        [Test]
        public void HealWorkspaceName_FollowsFolderRename()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            var oldDir = $"Assets/Xestrel/{state.avatarName}";

            AssetDatabase.RenameAsset(oldDir, "RenamedWS");
            WorkspaceManifests.HealWorkspaceName(state);

            Assert.That(state.avatarName, Is.EqualTo("RenamedWS"));

            // New copies keep landing in the renamed folder instead of recreating the old one.
            var other = new Material(Shader.Find("Unlit/Texture")) { name = "OtherMat" };
            AssetDatabase.CreateAsset(other, TestSourceDir + "/Other.mat");
            var mr = _avatar.GetComponentInChildren<MeshRenderer>();
            mr.sharedMaterials = new[] { mr.sharedMaterials[0], other };

            MaterialIsolator.Isolate(_avatar);

            var newCopy = _avatar.GetComponentInChildren<MeshRenderer>().sharedMaterials[1];
            Assert.That(AssetDatabase.GetAssetPath(newCopy), Does.StartWith("Assets/Xestrel/RenamedWS/"));
        }

        [Test]
        public void PruneDeadBindings_KeepsGuidHistoryInManifest()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            TextureIsolator.IsolateProperty(state, state.bindings[0].copy, "_MainTex");
            var manifest = (XestrelWorkspaceManifest)state.workspaceManifest;

            var copyTex = state.textureBindings[0].copy;
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(copyTex, out var copyGuid, out long _), Is.True);
            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(copyTex));
            Isolator.PruneDeadBindings(state);

            Assert.That(state.textureBindings, Is.Empty);
            Assert.That(manifest.textureBindings, Is.Empty, "the mirror follows the component");

            XestrelGuidRecord record = null;
            foreach (var r in manifest.guidHistory)
            {
                if (r.kind == "texture" && r.copyGuid == copyGuid) record = r;
            }
            Assert.That(record, Is.Not.Null, "the pruned pair must stay in guidHistory");
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(_sourceTex, out var texGuid, out long _), Is.True);
            Assert.That(record.originalGuid, Is.EqualTo(texGuid));
        }
    }
}
