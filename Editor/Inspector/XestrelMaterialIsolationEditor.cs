using UnityEditor;
using UnityEngine;
using Xestrel.Isolation;
using Xestrel.Runtime;
using Xestrel.UI;

namespace Xestrel.Inspector
{
    [CustomEditor(typeof(XestrelMaterialIsolation))]
    internal sealed class XestrelMaterialIsolationEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var state = (XestrelMaterialIsolation)target;

            EditorGUILayout.LabelField("Avatar", state.avatarName ?? "<unset>");
            EditorGUILayout.LabelField("Materials", (state.bindings?.Count ?? 0).ToString());
            EditorGUILayout.LabelField("Textures", (state.textureBindings?.Count ?? 0).ToString());
            EditorGUILayout.LabelField("Animators", (state.animatorBindings?.Count ?? 0).ToString());
            EditorGUILayout.LabelField("Clips", (state.clipBindings?.Count ?? 0).ToString());

            if (Isolator.HasDeadBindings(state))
            {
                EditorGUILayout.Space();
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox("Some bindings reference deleted assets.", MessageType.Warning);
                    if (GUILayout.Button("Prune", GUILayout.Width(60), GUILayout.Height(38)))
                    {
                        Isolator.PruneDeadBindings(state);
                    }
                }
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Window"))
                {
                    XestrelIsolationWindow.OpenFor(state);
                }
                if (GUILayout.Button("Re-isolate"))
                {
                    Isolator.Isolate(state.gameObject);
                }
                if (GUILayout.Button("Restore"))
                {
                    if (EditorUtility.DisplayDialog(
                            "Xestrel — Restore",
                            $"Point \"{state.avatarName}\" back at the original shared assets?\n\n" +
                            "All material / texture / animator bindings will be cleared. " +
                            "Copy assets under Assets/Xestrel/ stay on disk.",
                            "Restore", "Cancel"))
                    {
                        Isolator.Restore(state);
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "This component is stripped on VRChat upload via IEditorOnly. Per-avatar copies under " +
                "Assets/Xestrel/ are referenced by the avatar's Renderers / VRCAvatarDescriptor and are uploaded normally.",
                MessageType.Info);
        }
    }
}
