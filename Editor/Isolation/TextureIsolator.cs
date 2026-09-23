using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Xestrel.Core;
using Xestrel.Runtime;

namespace Xestrel.Isolation
{
    /// <summary>
    /// Per-texture isolation: copies a single Texture referenced by an isolated
    /// copy material into <c>Assets/Xestrel/&lt;AvatarName&gt;/Textures/</c> and
    /// rewires that property. Invoked on demand from the editor window — never
    /// in bulk — so untouched textures keep referring to the shared original.
    /// </summary>
    internal static class TextureIsolator
    {
        /// <summary>
        /// Replace the texture at <paramref name="propName"/> on <paramref name="copyMaterial"/>
        /// with a per-avatar copy. No-op when the property is empty or already
        /// references an isolated texture. Returns the texture now bound to that property.
        /// </summary>
        public static Texture IsolateProperty(XestrelMaterialIsolation state, Material copyMaterial, string propName)
        {
            if (state == null || copyMaterial == null || string.IsNullOrEmpty(propName)) return null;
            WorkspaceManifests.HealWorkspaceName(state);

            var current = copyMaterial.GetTexture(propName);
            if (current == null) return null;

            var path = AssetDatabase.GetAssetPath(current);
            if (IsolationPaths.IsUnderIsolationRoot(path)) return current;

            // Reuse an existing copy if we already isolated this source somewhere.
            var copy = FindRecordedCopy(state, current);

            if (copy == null)
            {
                var dir = IsolationPaths.TexturesDir(state.avatarName);
                XestrelPaths.EnsureDirectory(dir);
                var dst = dir + "/" + XestrelPaths.SanitiseFileSegment(current.name);
                copy = TextureCopyFactory.Create(current, dst);
                if (copy == null) return current;

                Undo.RecordObject(state, "Xestrel Isolate Texture");
                if (state.textureBindings == null)
                    state.textureBindings = new List<XestrelTextureBinding>();
                state.textureBindings.Add(new XestrelTextureBinding { original = current, copy = copy });
                EditorUtility.SetDirty(state);
            }

            Undo.RecordObject(copyMaterial, "Xestrel Isolate Texture");
            copyMaterial.SetTexture(propName, copy);
            EditorUtility.SetDirty(copyMaterial);
            WorkspaceManifests.Sync(state);
            AssetDatabase.SaveAssets();
            return copy;
        }

        /// <summary>
        /// Isolate every visible, non-empty texture property on <paramref name="copyMaterial"/>
        /// that still points at a shared texture. Returns the number of properties rewired.
        /// </summary>
        public static int IsolateAllProperties(XestrelMaterialIsolation state, Material copyMaterial)
        {
            if (state == null || copyMaterial == null) return 0;
            var shader = copyMaterial.shader;
            if (shader == null) return 0;

            int rewired = 0;
            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                if (ShaderUtil.IsShaderPropertyHidden(shader, i)) continue;

                var propName = ShaderUtil.GetPropertyName(shader, i);
                var current = copyMaterial.GetTexture(propName);
                if (current == null) continue;
                if (IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(current))) continue;

                var result = IsolateProperty(state, copyMaterial, propName);
                if (result != null && !ReferenceEquals(result, current)) rewired++;
            }
            if (rewired > 0)
            {
                XestrelLog.Info(XestrelLogCategory.Isolate,
                    $"Isolated {rewired} texture propert(ies) on '{copyMaterial.name}'");
            }
            return rewired;
        }

        /// <summary>
        /// Isolate one shared texture everywhere it appears: every texture property on
        /// every copy material that references <paramref name="texture"/> is repointed
        /// at a single per-avatar copy. Returns the number of properties rewired.
        /// </summary>
        public static int IsolateTextureAcrossMaterials(XestrelMaterialIsolation state, Texture texture)
        {
            if (state == null || texture == null || state.bindings == null) return 0;
            if (IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(texture))) return 0;

            int rewired = 0;
            foreach (var mb in state.bindings)
            {
                if (mb == null || mb.copy == null) continue;
                var shader = mb.copy.shader;
                if (shader == null) continue;
                int count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    var propName = ShaderUtil.GetPropertyName(shader, i);
                    if (mb.copy.GetTexture(propName) != texture) continue;
                    var result = IsolateProperty(state, mb.copy, propName);
                    if (result != null && !ReferenceEquals(result, texture)) rewired++;
                }
            }
            if (rewired > 0)
            {
                XestrelLog.Info(XestrelLogCategory.Isolate,
                    $"Isolated texture '{texture.name}' across materials: {rewired} propert(ies) rewired");
            }
            return rewired;
        }

        /// <summary>
        /// Revert one isolated texture copy everywhere it appears: every texture property
        /// on every copy material that references <paramref name="copy"/> is pointed back
        /// at the recorded original. The binding is kept for later reuse. Returns the
        /// number of properties rewired.
        /// </summary>
        public static int RestoreTextureAcrossMaterials(XestrelMaterialIsolation state, Texture copy)
        {
            var original = FindOriginal(state, copy);
            if (original == null || state.bindings == null) return 0;

            int rewired = 0;
            foreach (var mb in state.bindings)
            {
                if (mb == null || mb.copy == null) continue;
                var shader = mb.copy.shader;
                if (shader == null) continue;
                int count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    var propName = ShaderUtil.GetPropertyName(shader, i);
                    if (mb.copy.GetTexture(propName) != copy) continue;
                    Undo.RecordObject(mb.copy, "Xestrel Restore Texture");
                    mb.copy.SetTexture(propName, original);
                    EditorUtility.SetDirty(mb.copy);
                    rewired++;
                }
            }
            if (rewired > 0)
            {
                AssetDatabase.SaveAssets();
                XestrelLog.Info(XestrelLogCategory.Isolate,
                    $"Restored texture '{original.name}' across materials: {rewired} propert(ies) reverted");
            }
            return rewired;
        }

        /// <summary>
        /// Find the recorded original for an isolated texture copy, or null when the
        /// texture is not one of this avatar's copies.
        /// </summary>
        public static Texture FindOriginal(XestrelMaterialIsolation state, Texture copy)
        {
            if (state == null || copy == null || state.textureBindings == null) return null;
            foreach (var b in state.textureBindings)
            {
                if (b != null && b.copy == copy && b.original != null) return b.original;
            }
            return null;
        }

        /// <summary>
        /// Find the copy already recorded for a shared <paramref name="original"/>, or
        /// null when this avatar has never isolated it. Isolating a property whose
        /// texture has a recorded copy reuses that copy instead of creating a new one.
        /// </summary>
        public static Texture FindRecordedCopy(XestrelMaterialIsolation state, Texture original)
        {
            if (state == null || original == null || state.textureBindings == null) return null;
            foreach (var b in state.textureBindings)
            {
                if (b != null && b.original == original && b.copy != null) return b.copy;
            }
            return null;
        }

        /// <summary>
        /// Count the texture properties on this avatar's copy materials that point at a
        /// shared original even though a copy of it is recorded — e.g. after a per-slot
        /// Restore, or a Restore on an unforked duplicate that shares the copy materials.
        /// </summary>
        public static int CountReapplicable(XestrelMaterialIsolation state) =>
            WalkReapplicable(state, apply: false);

        /// <summary>
        /// Point every property counted by <see cref="CountReapplicable"/> back at its
        /// recorded copy. Never creates new copies. Returns the number of properties rewired.
        /// </summary>
        public static int ReapplyRecordedCopies(XestrelMaterialIsolation state)
        {
            var rewired = WalkReapplicable(state, apply: true);
            if (rewired > 0)
            {
                AssetDatabase.SaveAssets();
                XestrelLog.Info(XestrelLogCategory.Isolate,
                    $"Re-applied recorded texture copies on '{state.avatarName}': {rewired} propert(ies) rewired");
            }
            return rewired;
        }

        private static int WalkReapplicable(XestrelMaterialIsolation state, bool apply)
        {
            if (state == null || state.bindings == null ||
                state.textureBindings == null || state.textureBindings.Count == 0) return 0;

            int n = 0;
            foreach (var mb in state.bindings)
            {
                if (mb == null || mb.copy == null) continue;
                var shader = mb.copy.shader;
                if (shader == null) continue;
                bool recorded = false;
                int count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    var propName = ShaderUtil.GetPropertyName(shader, i);
                    var copy = FindRecordedCopy(state, mb.copy.GetTexture(propName));
                    if (copy == null) continue;
                    n++;
                    if (!apply) continue;
                    if (!recorded) Undo.RecordObject(mb.copy, "Xestrel Re-apply Textures");
                    recorded = true;
                    mb.copy.SetTexture(propName, copy);
                    EditorUtility.SetDirty(mb.copy);
                }
            }
            return n;
        }

        /// <summary>
        /// Revert a single texture property back to its recorded original. The binding
        /// is kept so a later re-isolation reuses the same copy asset. No-op when the
        /// property is empty or not bound to one of this avatar's copies.
        /// Returns the texture now bound to that property.
        /// </summary>
        public static Texture RestoreProperty(XestrelMaterialIsolation state, Material copyMaterial, string propName)
        {
            if (state == null || copyMaterial == null || string.IsNullOrEmpty(propName)) return null;

            var current = copyMaterial.GetTexture(propName);
            var original = FindOriginal(state, current);
            if (original == null) return current;

            Undo.RecordObject(copyMaterial, "Xestrel Restore Texture");
            copyMaterial.SetTexture(propName, original);
            EditorUtility.SetDirty(copyMaterial);
            AssetDatabase.SaveAssets();
            return original;
        }

        /// <summary>
        /// Clear the recorded texture bindings as part of a full Restore. The copy
        /// materials are deliberately left untouched: the renderers are pointed back at
        /// the original materials anyway, and a copy <c>.mat</c> is a project asset that
        /// may still be used by another avatar sharing this workspace — e.g. an unforked
        /// duplicate in another (possibly unloaded) scene. Rewriting its texture slots
        /// here would silently revert that avatar's textures too. Copy texture assets on
        /// disk are not deleted.
        /// </summary>
        public static void Restore(XestrelMaterialIsolation state)
        {
            if (state == null) return;
            var cleared = state.textureBindings?.Count ?? 0;

            Undo.RecordObject(state, "Xestrel Restore Textures");
            state.textureBindings = new List<XestrelTextureBinding>();
            EditorUtility.SetDirty(state);
            XestrelLog.Info(XestrelLogCategory.Isolate,
                $"Cleared {cleared} texture binding(s) for '{state.avatarName}' (copy materials and texture assets left as-is)");
        }
    }
}
