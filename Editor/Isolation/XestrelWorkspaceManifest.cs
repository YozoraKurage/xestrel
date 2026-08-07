using System;
using System.Collections.Generic;
using UnityEngine;
using Xestrel.Runtime;

namespace Xestrel.Isolation
{
    /// <summary>
    /// A copy→original GUID pair remembered forever. Unlike the live binding lists,
    /// records are never removed — even after a binding is pruned because an asset
    /// went missing, the relationship stays reconstructible from here.
    /// </summary>
    [Serializable]
    internal class XestrelGuidRecord
    {
        public string kind; // "material" / "texture" / "animator" / "clip"
        public string copyGuid;
        public string originalGuid;
    }

    /// <summary>
    /// On-disk mirror of one avatar's <see cref="XestrelMaterialIsolation"/> component,
    /// stored as <c>Assets/Xestrel/&lt;AvatarName&gt;/XestrelWorkspace.asset</c>. The scene
    /// component stays authoritative; this asset exists so the original→copy mapping
    /// survives losing the component (prefab revert, scene mishap) and can be recovered
    /// from the window. See <see cref="WorkspaceManifests"/> for sync and recovery.
    /// </summary>
    internal sealed class XestrelWorkspaceManifest : ScriptableObject
    {
        public const string FileName = "XestrelWorkspace.asset";

        public string avatarName;
        // Identity of the avatar that last synced, for picking a recover candidate.
        public string avatarObjectName;
        public string avatarGlobalId;

        public List<XestrelMaterialBinding> bindings = new List<XestrelMaterialBinding>();
        public List<XestrelTextureBinding> textureBindings = new List<XestrelTextureBinding>();
        public List<XestrelAnimatorBinding> animatorBindings = new List<XestrelAnimatorBinding>();
        public List<XestrelClipBinding> clipBindings = new List<XestrelClipBinding>();

        public List<XestrelGuidRecord> guidHistory = new List<XestrelGuidRecord>();
    }
}
