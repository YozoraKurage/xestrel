using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Xestrel.Core;
using Xestrel.Isolation;
using Xestrel.Runtime;

namespace Xestrel.Tests
{
    public class MaterialIsolatorTests
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
        public void MaterialIsolate_AddsComponentAndRewiresRenderer()
        {
            MaterialIsolator.Isolate(_avatar);

            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            Assert.That(state, Is.Not.Null, "component should be added");
            Assert.That(state.bindings.Count, Is.EqualTo(1));
            Assert.That(state.bindings[0].original, Is.SameAs(_sourceMat));

            var mr = _avatar.GetComponentInChildren<MeshRenderer>();
            var copy = mr.sharedMaterials[0];
            Assert.That(copy, Is.Not.SameAs(_sourceMat), "renderer should reference the copy");
            var copyPath = AssetDatabase.GetAssetPath(copy);
            Assert.That(copyPath, Does.StartWith("Assets/Xestrel/"));
        }

        [Test]
        public void MaterialIsolate_IsIdempotent()
        {
            MaterialIsolator.Isolate(_avatar);
            var firstCount = _avatar.GetComponent<XestrelMaterialIsolation>().bindings.Count;
            var firstCopy = _avatar.GetComponentInChildren<MeshRenderer>().sharedMaterials[0];

            MaterialIsolator.Isolate(_avatar);

            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            Assert.That(state.bindings.Count, Is.EqualTo(firstCount));
            Assert.That(_avatar.GetComponentInChildren<MeshRenderer>().sharedMaterials[0],
                Is.SameAs(firstCopy), "second Isolate should not create a new copy");
        }

        [Test]
        public void MaterialRestore_RevertsToOriginal()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();

            MaterialIsolator.Restore(state);

            var mr = _avatar.GetComponentInChildren<MeshRenderer>();
            Assert.That(mr.sharedMaterials[0], Is.SameAs(_sourceMat));
            Assert.That(state.bindings, Is.Empty);
        }

        [Test]
        public void Isolator_OnlyIsolatesMaterialsByDefault()
        {
            Isolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();

            Assert.That(state.bindings.Count, Is.EqualTo(1));
            Assert.That(state.textureBindings, Is.Empty,
                "textures are isolated on demand, not in bulk");
            Assert.That(state.bindings[0].copy.mainTexture, Is.SameAs(_sourceTex),
                "copy material still references the shared source texture until the user isolates it");
        }

        [Test]
        public void TextureIsolator_IsolateProperty_CopiesAndRewires()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            var copyMat = state.bindings[0].copy;

            var result = TextureIsolator.IsolateProperty(state, copyMat, "_MainTex");

            Assert.That(result, Is.Not.Null);
            Assert.That(result, Is.Not.SameAs(_sourceTex));
            Assert.That(AssetDatabase.GetAssetPath(result), Does.StartWith("Assets/Xestrel/"));
            Assert.That(copyMat.mainTexture, Is.SameAs(result));
            Assert.That(state.textureBindings.Count, Is.EqualTo(1));
            Assert.That(state.textureBindings[0].original, Is.SameAs(_sourceTex));
        }

        [Test]
        public void TextureIsolator_IsolateProperty_IsIdempotent()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            var copyMat = state.bindings[0].copy;

            var first = TextureIsolator.IsolateProperty(state, copyMat, "_MainTex");
            var second = TextureIsolator.IsolateProperty(state, copyMat, "_MainTex");

            Assert.That(second, Is.SameAs(first));
            Assert.That(state.textureBindings.Count, Is.EqualTo(1));
        }

        [Test]
        public void MaterialIsolate_CopiesBuiltinDefaultMaterial()
        {
            var builtin = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
            var mr = _avatar.GetComponentInChildren<MeshRenderer>();
            mr.sharedMaterials = new[] { builtin };

            MaterialIsolator.Isolate(_avatar);

            var copy = _avatar.GetComponentInChildren<MeshRenderer>().sharedMaterials[0];
            Assert.That(copy, Is.Not.SameAs(builtin),
                "built-in materials cannot stay shared; an editable copy should be saved");
            Assert.That(AssetDatabase.GetAssetPath(copy), Does.StartWith("Assets/Xestrel/"));
            Assert.That(copy.hideFlags, Is.EqualTo(HideFlags.None));
        }

        [Test]
        public void CountPendingMaterials_ReflectsIsolationState()
        {
            Assert.That(MaterialIsolator.CountPendingMaterials(_avatar), Is.EqualTo(1));

            MaterialIsolator.Isolate(_avatar);

            Assert.That(MaterialIsolator.CountPendingMaterials(_avatar), Is.EqualTo(0));
        }

        [Test]
        public void Isolate_KeepsWorkspaceFolderWhenAvatarRenamed()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            var workspace = state.avatarName;

            _avatar.name = "RenamedAvatar";
            var other = new Material(Shader.Find("Unlit/Texture")) { name = "OtherMat" };
            AssetDatabase.CreateAsset(other, TestSourceDir + "/Other.mat");
            var mr = _avatar.GetComponentInChildren<MeshRenderer>();
            mr.sharedMaterials = new[] { mr.sharedMaterials[0], other };

            MaterialIsolator.Isolate(_avatar);

            Assert.That(state.avatarName, Is.EqualTo(workspace),
                "the workspace name is fixed at first isolation");
            var newCopy = _avatar.GetComponentInChildren<MeshRenderer>().sharedMaterials[1];
            Assert.That(AssetDatabase.GetAssetPath(newCopy),
                Does.StartWith($"Assets/Xestrel/{workspace}/"),
                "new copies keep landing in the original folder");
            Assert.That(AssetDatabase.IsValidFolder("Assets/Xestrel/RenamedAvatar"), Is.False);
        }

        [Test]
        public void Isolate_PicksUniqueWorkspaceWhenFolderNameIsTaken()
        {
            // Simulate a same-named avatar's folder from another scene / older session.
            XestrelPaths.EnsureDirectory("Assets/Xestrel/TestAvatar");

            MaterialIsolator.Isolate(_avatar);

            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            Assert.That(state.avatarName, Is.EqualTo("TestAvatar (1)"));
            var copy = _avatar.GetComponentInChildren<MeshRenderer>().sharedMaterials[0];
            Assert.That(AssetDatabase.GetAssetPath(copy),
                Does.StartWith("Assets/Xestrel/TestAvatar (1)/"));
        }

        [Test]
        public void CollectPendingMaterials_CountsSlots()
        {
            var mr = _avatar.GetComponentInChildren<MeshRenderer>();
            mr.sharedMaterials = new[] { _sourceMat, _sourceMat };

            var pending = MaterialIsolator.CollectPendingMaterials(_avatar);

            Assert.That(pending.Count, Is.EqualTo(1));
            Assert.That(pending[0].Key, Is.SameAs(_sourceMat));
            Assert.That(pending[0].Value, Is.EqualTo(2));
        }

        [Test]
        public void MaterialIsolateSingle_RewiresOnlyThatMaterial()
        {
            var other = new Material(Shader.Find("Unlit/Texture")) { name = "OtherMat" };
            AssetDatabase.CreateAsset(other, TestSourceDir + "/Other.mat");
            var mr = _avatar.GetComponentInChildren<MeshRenderer>();
            mr.sharedMaterials = new[] { _sourceMat, other };

            var copy = MaterialIsolator.IsolateSingle(_avatar, _sourceMat);

            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            Assert.That(state, Is.Not.Null, "state component should be added on demand");
            Assert.That(state.bindings.Count, Is.EqualTo(1));
            var mats = _avatar.GetComponentInChildren<MeshRenderer>().sharedMaterials;
            Assert.That(mats[0], Is.SameAs(copy));
            Assert.That(mats[1], Is.SameAs(other), "the other material must stay shared");
            Assert.That(MaterialIsolator.CountPendingMaterials(_avatar), Is.EqualTo(1));

            Assert.That(MaterialIsolator.IsolateSingle(_avatar, _sourceMat), Is.SameAs(copy),
                "second IsolateSingle should reuse the recorded copy");
            Assert.That(state.bindings.Count, Is.EqualTo(1));
        }

        [Test]
        public void TextureIsolator_IsolateTextureAcrossMaterials_RewiresEverySlotToOneCopy()
        {
            var other = new Material(Shader.Find("Unlit/Texture")) { name = "OtherMat", mainTexture = _sourceTex };
            AssetDatabase.CreateAsset(other, TestSourceDir + "/Other.mat");
            var mr = _avatar.GetComponentInChildren<MeshRenderer>();
            mr.sharedMaterials = new[] { _sourceMat, other };
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();

            var rewired = TextureIsolator.IsolateTextureAcrossMaterials(state, _sourceTex);

            Assert.That(rewired, Is.EqualTo(2));
            var texA = state.bindings[0].copy.mainTexture;
            var texB = state.bindings[1].copy.mainTexture;
            Assert.That(texA, Is.Not.SameAs(_sourceTex));
            Assert.That(texB, Is.SameAs(texA), "both slots should share a single per-avatar copy");
            Assert.That(state.textureBindings.Count, Is.EqualTo(1));
        }

        [Test]
        public void TextureIsolator_RestoreTextureAcrossMaterials_RevertsEverySlot()
        {
            var other = new Material(Shader.Find("Unlit/Texture")) { name = "OtherMat", mainTexture = _sourceTex };
            AssetDatabase.CreateAsset(other, TestSourceDir + "/Other.mat");
            var mr = _avatar.GetComponentInChildren<MeshRenderer>();
            mr.sharedMaterials = new[] { _sourceMat, other };
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            TextureIsolator.IsolateTextureAcrossMaterials(state, _sourceTex);
            var copyTex = state.textureBindings[0].copy;

            var rewired = TextureIsolator.RestoreTextureAcrossMaterials(state, copyTex);

            Assert.That(rewired, Is.EqualTo(2));
            Assert.That(state.bindings[0].copy.mainTexture, Is.SameAs(_sourceTex));
            Assert.That(state.bindings[1].copy.mainTexture, Is.SameAs(_sourceTex));
            Assert.That(state.textureBindings.Count, Is.EqualTo(1),
                "binding is kept so re-isolation reuses the copy asset");
        }

        [Test]
        public void TextureIsolator_IsolateAllProperties_IsolatesEveryTextureSlot()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            var copyMat = state.bindings[0].copy;

            var rewired = TextureIsolator.IsolateAllProperties(state, copyMat);

            Assert.That(rewired, Is.EqualTo(1), "Unlit/Texture has a single texture slot");
            Assert.That(copyMat.mainTexture, Is.Not.SameAs(_sourceTex));
            Assert.That(AssetDatabase.GetAssetPath(copyMat.mainTexture), Does.StartWith("Assets/Xestrel/"));

            Assert.That(TextureIsolator.IsolateAllProperties(state, copyMat), Is.EqualTo(0),
                "second run should find nothing left to isolate");
        }

        [Test]
        public void TextureIsolator_RestoreProperty_RevertsAndKeepsBindingForReuse()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            var copyMat = state.bindings[0].copy;
            var copyTex = TextureIsolator.IsolateProperty(state, copyMat, "_MainTex");

            var restored = TextureIsolator.RestoreProperty(state, copyMat, "_MainTex");

            Assert.That(restored, Is.SameAs(_sourceTex));
            Assert.That(copyMat.mainTexture, Is.SameAs(_sourceTex));
            Assert.That(state.textureBindings.Count, Is.EqualTo(1),
                "binding is kept so re-isolation reuses the copy asset");

            var again = TextureIsolator.IsolateProperty(state, copyMat, "_MainTex");
            Assert.That(again, Is.SameAs(copyTex), "re-isolation should reuse the existing copy");
        }

        [Test]
        public void MaterialRestoreBinding_RevertsSingleMaterialOnly()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            var binding = state.bindings[0];

            MaterialIsolator.RestoreBinding(state, binding);

            var mr = _avatar.GetComponentInChildren<MeshRenderer>();
            Assert.That(mr.sharedMaterials[0], Is.SameAs(_sourceMat));
            Assert.That(state.bindings, Is.Empty);
        }

        [Test]
        public void Fork_GivesDuplicateIndependentCopies()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            TextureIsolator.IsolateProperty(state, state.bindings[0].copy, "_MainTex");
            var sourceCopyMat = state.bindings[0].copy;
            var sourceCopyTex = state.textureBindings[0].copy;

            var dup = Object.Instantiate(_avatar);
            dup.name = "TestAvatarVariant";
            var dupState = dup.GetComponent<XestrelMaterialIsolation>();
            try
            {
                Assert.That(dup.GetComponentInChildren<MeshRenderer>().sharedMaterials[0],
                    Is.SameAs(sourceCopyMat), "precondition: the duplicate shares the copy");

                var ok = WorkspaceForker.Fork(dupState);

                Assert.That(ok, Is.True);
                Assert.That(dupState.avatarName, Is.Not.EqualTo(state.avatarName));

                var forkMat = dup.GetComponentInChildren<MeshRenderer>().sharedMaterials[0];
                Assert.That(forkMat, Is.Not.SameAs(sourceCopyMat));
                Assert.That(AssetDatabase.GetAssetPath(forkMat),
                    Does.StartWith($"Assets/Xestrel/{dupState.avatarName}/"));

                Assert.That(forkMat.mainTexture, Is.Not.SameAs(sourceCopyTex),
                    "the texture copy must be forked too");
                Assert.That(AssetDatabase.GetAssetPath(forkMat.mainTexture),
                    Does.StartWith($"Assets/Xestrel/{dupState.avatarName}/"));

                Assert.That(dupState.bindings[0].original, Is.SameAs(_sourceMat),
                    "fork bindings keep pointing at the true shared original");
                Assert.That(dupState.bindings[0].copy, Is.SameAs(forkMat));
                Assert.That(dupState.textureBindings[0].original, Is.SameAs(_sourceTex));
                Assert.That(dupState.textureBindings[0].copy, Is.SameAs(forkMat.mainTexture));

                // The source avatar and its workspace are untouched.
                Assert.That(_avatar.GetComponentInChildren<MeshRenderer>().sharedMaterials[0],
                    Is.SameAs(sourceCopyMat));
                Assert.That(sourceCopyMat.mainTexture, Is.SameAs(sourceCopyTex));
                Assert.That(state.bindings[0].copy, Is.SameAs(sourceCopyMat));
            }
            finally
            {
                Object.DestroyImmediate(dup);
            }
        }

        [Test]
        public void PruneDeadBindings_RemovesBindingsWithDeletedAssets()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            TextureIsolator.IsolateProperty(state, state.bindings[0].copy, "_MainTex");
            Assert.That(Isolator.HasDeadBindings(state), Is.False);

            var copyTexPath = AssetDatabase.GetAssetPath(state.textureBindings[0].copy);
            AssetDatabase.DeleteAsset(copyTexPath);

            Assert.That(Isolator.HasDeadBindings(state), Is.True);
            var removed = Isolator.PruneDeadBindings(state);

            Assert.That(removed, Is.EqualTo(1));
            Assert.That(state.textureBindings, Is.Empty);
            Assert.That(state.bindings.Count, Is.EqualTo(1), "material binding is still alive");
            Assert.That(Isolator.HasDeadBindings(state), Is.False);
        }

        [Test]
        public void Isolator_RestoreRevertsBoth()
        {
            MaterialIsolator.Isolate(_avatar);
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            var copyMat = state.bindings[0].copy;
            TextureIsolator.IsolateProperty(state, copyMat, "_MainTex");

            Isolator.Restore(state);

            Assert.That(_avatar.GetComponentInChildren<MeshRenderer>().sharedMaterials[0], Is.SameAs(_sourceMat));
            Assert.That(state.bindings, Is.Empty);
            Assert.That(state.textureBindings, Is.Empty);
            Assert.That(copyMat.mainTexture, Is.SameAs(_sourceTex),
                "copy material's texture reference should be restored before the renderer is reverted");
        }
    }

    public class AnimatorIsolatorTests
    {
        private const string TestSourceDir = "Assets/XestrelTests";
        private const string TestSourceClip = "Assets/XestrelTests/SourceClip.anim";
        private const string TestSourceCtrl = "Assets/XestrelTests/SourceCtrl.controller";

        private GameObject _avatar;
        private AnimationClip _sourceClip;
        private UnityEditor.Animations.AnimatorController _sourceCtrl;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestSourceDir))
                AssetDatabase.CreateFolder("Assets", "XestrelTests");

            _sourceClip = new AnimationClip { name = "SourceClip" };
            AssetDatabase.CreateAsset(_sourceClip, TestSourceClip);

            _sourceCtrl = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(TestSourceCtrl);
            var sm = _sourceCtrl.layers[0].stateMachine;
            var st = sm.AddState("StateA");
            st.motion = _sourceClip;
            AssetDatabase.SaveAssets();

            _avatar = new GameObject("TestAvatar");
            // The state component is normally added by MaterialIsolator; for animator tests
            // we add it directly so we don't need any renderers.
            var state = _avatar.AddComponent<XestrelMaterialIsolation>();
            state.avatarName = _avatar.name;
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
        public void IsolateController_CopiesControllerAndRewiresClips()
        {
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();

            var copy = AnimatorIsolator.IsolateController(state, _sourceCtrl);

            Assert.That(copy, Is.Not.Null);
            Assert.That(copy, Is.Not.SameAs(_sourceCtrl));
            Assert.That(AssetDatabase.GetAssetPath(copy), Does.StartWith("Assets/Xestrel/"));

            var copyState = copy.layers[0].stateMachine.states[0].state;
            var copyMotion = copyState.motion as AnimationClip;
            Assert.That(copyMotion, Is.Not.Null);
            Assert.That(copyMotion, Is.Not.SameAs(_sourceClip));
            Assert.That(AssetDatabase.GetAssetPath(copyMotion), Does.StartWith("Assets/Xestrel/"));

            Assert.That(state.animatorBindings.Count, Is.EqualTo(1));
            Assert.That(state.animatorBindings[0].original, Is.SameAs(_sourceCtrl));
            Assert.That(state.clipBindings.Count, Is.EqualTo(1));
            Assert.That(state.clipBindings[0].original, Is.SameAs(_sourceClip));
        }

        [Test]
        public void IsolateController_IsIdempotent()
        {
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();

            var first = AnimatorIsolator.IsolateController(state, _sourceCtrl);
            var second = AnimatorIsolator.IsolateController(state, _sourceCtrl);

            Assert.That(second, Is.SameAs(first));
            Assert.That(state.animatorBindings.Count, Is.EqualTo(1));
            Assert.That(state.clipBindings.Count, Is.EqualTo(1));
        }

        [Test]
        public void IsolateController_DoesNotRecordAlreadyIsolatedClipsAsBindings()
        {
            // A clip already under Assets/Xestrel stays shared; it must not end up as a
            // self-referential clip binding (original == copy).
            XestrelPaths.EnsureDirectory("Assets/Xestrel/Shared");
            var isolatedClip = new AnimationClip { name = "AlreadyIsolated" };
            AssetDatabase.CreateAsset(isolatedClip, "Assets/Xestrel/Shared/AlreadyIsolated.anim");
            var st = _sourceCtrl.layers[0].stateMachine.AddState("StateB");
            st.motion = isolatedClip;
            AssetDatabase.SaveAssets();
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();

            var copy = AnimatorIsolator.IsolateController(state, _sourceCtrl);

            Assert.That(copy.layers[0].stateMachine.states[1].state.motion, Is.SameAs(isolatedClip),
                "the already-isolated clip stays referenced as-is");
            Assert.That(state.clipBindings.Count, Is.EqualTo(1),
                "only the truly copied clip is recorded");
            Assert.That(state.clipBindings[0].original, Is.SameAs(_sourceClip));
        }

        [Test]
        public void Restore_ClearsAnimatorAndClipBindings()
        {
            var state = _avatar.GetComponent<XestrelMaterialIsolation>();
            AnimatorIsolator.IsolateController(state, _sourceCtrl);

            AnimatorIsolator.Restore(state);

            Assert.That(state.animatorBindings, Is.Empty);
            Assert.That(state.clipBindings, Is.Empty);
        }
    }
}
