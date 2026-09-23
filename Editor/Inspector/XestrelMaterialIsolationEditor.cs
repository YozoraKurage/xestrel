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

            EditorGUILayout.LabelField("アバター", state.avatarName ?? "<未設定>");
            EditorGUILayout.LabelField("マテリアル", (state.bindings?.Count ?? 0).ToString());
            EditorGUILayout.LabelField("テクスチャ", (state.textureBindings?.Count ?? 0).ToString());
            EditorGUILayout.LabelField("アニメーター", (state.animatorBindings?.Count ?? 0).ToString());
            EditorGUILayout.LabelField("アニメーションクリップ", (state.clipBindings?.Count ?? 0).ToString());

            if (Isolator.HasDeadBindings(state))
            {
                EditorGUILayout.Space();
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox("削除されたアセットを指している対応記録があります。", MessageType.Warning);
                    if (GUILayout.Button(new GUIContent("記録を\n掃除", "削除済みアセットを指す対応記録を取り除きます"),
                            GUILayout.Width(60), GUILayout.Height(38)))
                    {
                        Isolator.PruneDeadBindings(state);
                    }
                }
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("ウィンドウを開く"))
                {
                    XestrelIsolationWindow.OpenFor(state);
                }
                if (GUILayout.Button(new GUIContent("専用コピーを作成", "まだ共有のままのマテリアルを専用コピーにします（専用化済みのものはそのまま）")))
                {
                    Isolator.Isolate(state.gameObject);
                }
                if (GUILayout.Button(new GUIContent("すべて元に戻す", "参照をコピー前の共有アセットに戻します（コピーはディスクに残ります）")))
                {
                    if (EditorUtility.DisplayDialog(
                            "Xestrel — すべて元に戻す",
                            $"「{state.avatarName}」の参照を、コピー前の共有アセットに戻しますか？\n\n" +
                            "マテリアル / テクスチャ / アニメーターの対応記録はすべて消去されます。" +
                            "Assets/Xestrel/ 以下のコピーファイルは削除されずに残ります。",
                            "元に戻す", "キャンセル"))
                    {
                        Isolator.Restore(state);
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "このコンポーネントは IEditorOnly のため VRChat アップロード時に除去されます。Assets/Xestrel/ 以下の" +
                "専用コピーはアバターの Renderer / VRCAvatarDescriptor から参照されるので、通常どおりアップロードされます。",
                MessageType.Info);
        }
    }
}
