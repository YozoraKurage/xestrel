using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace Xestrel.Runtime
{
    [Serializable]
    public class XestrelMaterialBinding
    {
        public Material original;
        public Material copy;
    }

    [Serializable]
    public class XestrelTextureBinding
    {
        public Texture original;
        public Texture copy;
    }

    [Serializable]
    public class XestrelAnimatorBinding
    {
        public RuntimeAnimatorController original;
        public RuntimeAnimatorController copy;
    }

    [Serializable]
    public class XestrelClipBinding
    {
        public AnimationClip original;
        public AnimationClip copy;
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Xestrel/Asset Isolation")]
    public sealed class XestrelMaterialIsolation : MonoBehaviour, IEditorOnly
    {
        public string avatarName;
        public List<XestrelMaterialBinding> bindings = new List<XestrelMaterialBinding>();
        public List<XestrelTextureBinding> textureBindings = new List<XestrelTextureBinding>();
        public List<XestrelAnimatorBinding> animatorBindings = new List<XestrelAnimatorBinding>();
        public List<XestrelClipBinding> clipBindings = new List<XestrelClipBinding>();
    }
}
