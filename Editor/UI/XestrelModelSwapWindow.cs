using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Xestrel.Detection;
using Xestrel.Isolation;
using Xestrel.Runtime;

namespace Xestrel.UI
{
    /// <summary>
    /// Swapping the FBX an avatar is built on. The check (<see cref="ModelSwapValidator"/>)
    /// is read-only and must run before the swap itself (<see cref="ModelSwapper"/>)
    /// is offered.
    /// </summary>
    internal sealed class XestrelModelSwapWindow : EditorWindow
    {
        private GameObject _avatar;
        private GameObject _oldModel;
        private GameObject _newModel;
        private ModelSwapReport _report;
        private Vector2 _scroll;
        private bool _showInfo;

        [MenuItem("Window/Xestrel/FBX Swap")]
        public static void Open()
        {
            var w = GetWindow<XestrelModelSwapWindow>();
            w.titleContent = new GUIContent("FBX 差し替え");
            w.minSize = new Vector2(440, 320);
            if (w._avatar == null && Selection.activeGameObject != null)
                w.SetAvatar(AvatarRootRecogniser.ResolveAvatarRoot(Selection.activeGameObject) ?? Selection.activeGameObject);
            w.Show();
        }

        public static void OpenFor(GameObject avatar)
        {
            var w = GetWindow<XestrelModelSwapWindow>();
            w.titleContent = new GUIContent("FBX 差し替え");
            w.minSize = new Vector2(440, 320);
            if (avatar != null) w.SetAvatar(avatar);
            w.Show();
        }

        private void SetAvatar(GameObject avatar)
        {
            _avatar = avatar;
            _oldModel = ModelSwapValidator.FindBaseModel(avatar);
            _report = null;
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "アバターの元になっている FBX を、ほぼ同じ別の FBX に差し替えます。まず「チェック」で" +
                "何が引き継がれないかを確認してください（チェックは何も書き換えません）。" +
                "差し替えても元の FBX・新しい FBX・元の Prefab は変更しません。",
                MessageType.None);

            if (DrawCurrentSwap()) return;

            using (var check = new EditorGUI.ChangeCheckScope())
            {
                var avatar = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("アバター", "シーン上のアバター。追加したオブジェクトや参照への影響も調べます（空でも FBX 同士の比較はできます）"),
                    _avatar, typeof(GameObject), true);
                if (check.changed) SetAvatar(avatar);
            }
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                _oldModel = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("元の FBX", "アバターを選ぶと自動で入ります"), _oldModel, typeof(GameObject), false);
                _newModel = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("新しい FBX", "差し替え先の FBX"), _newModel, typeof(GameObject), false);
                if (check.changed) _report = null;
            }

            if (_oldModel != null && _newModel != null && _oldModel == _newModel)
            {
                EditorGUILayout.HelpBox("元の FBX と新しい FBX が同じです。", MessageType.Warning);
            }
            else if (_newModel != null && !ModelSwapValidator.IsModelAsset(_newModel))
            {
                EditorGUILayout.HelpBox("新しい FBX がモデルファイルではありません（Prefab 同士としても比較はできます）。", MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(_oldModel == null || _newModel == null || _oldModel == _newModel))
            {
                if (GUILayout.Button("チェック", GUILayout.Height(24)))
                {
                    _report = ModelSwapValidator.Validate(_oldModel, _newModel, _avatar);
                }
            }

            if (_report == null) return;
            EditorGUILayout.Space(4f);
            DrawSummary();
            DrawSwapButton();
            DrawIssues();
        }

        // When the avatar is already swapped, show that and offer only the way back.
        private bool DrawCurrentSwap()
        {
            var state = _avatar != null ? _avatar.GetComponent<XestrelMaterialIsolation>() : null;
            var swap = ModelSwapper.CurrentSwap(state);
            if (swap == null) return false;

            var from = swap.originalModel != null ? swap.originalModel.name : "?";
            var to = swap.newModel != null ? swap.newModel.name : "(見つかりません)";
            var avatar = (GameObject)EditorGUILayout.ObjectField("アバター", _avatar, typeof(GameObject), true);
            if (avatar != _avatar)
            {
                SetAvatar(avatar);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.HelpBox(
                $"このアバターは FBX を差し替え済みです: {from} → {to}\n" +
                "別の FBX にしたい場合は、いったん元に戻してから差し替えてください。",
                MessageType.Info);
            if (GUILayout.Button("FBX を元に戻す", GUILayout.Height(24)) &&
                EditorUtility.DisplayDialog("Xestrel — FBX を元に戻す",
                    $"「{_avatar.name}」を差し替え前の Prefab（{from}）に戻しますか？\n\n" +
                    "シーン上で追加・変更した内容は、対応が取れる限り引き継がれます。",
                    "元に戻す", "キャンセル"))
            {
                ModelSwapper.Restore(state);
                SetAvatar(_avatar);
                GUIUtility.ExitGUI();
            }
            return true;
        }

        private void DrawSwapButton()
        {
            if (_avatar == null) return;
            var reason = ModelSwapper.CannotSwapReason(_avatar, _newModel);
            if (reason == null && ModelSwapValidator.FindBaseModel(_avatar) != _oldModel)
                reason = "「元の FBX」がアバターの実際の元 FBX と違います。アバターを選び直してください。";
            if (reason != null)
            {
                EditorGUILayout.HelpBox("差し替えできません: " + reason, MessageType.None);
                return;
            }

            var errors = _report.Count(SwapSeverity.Error);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (!GUILayout.Button(new GUIContent(errors > 0 ? "それでも差し替える" : "差し替える",
                            "アバターの Prefab を Assets/Xestrel/ にコピーし、コピー側だけ新しい FBX をベースにして、アバターをそちらに付け替えます"),
                        GUILayout.Height(24), GUILayout.Width(160)))
                    return;
            }

            var warning = errors > 0
                ? $"\n\n⚠ 失われるものが {errors} 件あります。差し替えるとアバターから外れます。" +
                  "Prefab 側で追加したものは元に戻せば復活しますが、シーン上で追加したものは復活しません（直後なら Ctrl+Z で取り消せます）。"
                : "";
            if (!EditorUtility.DisplayDialog("Xestrel — FBX を差し替え",
                    $"「{_avatar.name}」の FBX を「{_oldModel.name}」から「{_newModel.name}」に差し替えますか？\n\n" +
                    "アバターの Prefab は Assets/Xestrel/ 以下にコピーしてから差し替えるので、元の Prefab と FBX は変更しません。" +
                    "「元に戻す」でいつでも差し替え前に戻せます。" + warning,
                    "差し替える", "キャンセル"))
                return;

            if (ModelSwapper.Swap(_avatar, _newModel))
            {
                EditorUtility.DisplayDialog("Xestrel", "FBX を差し替えました。", "OK");
                _newModel = null;
            }
            else
            {
                EditorUtility.DisplayDialog("Xestrel",
                    "差し替えに失敗しました。アバターは変更していません。詳細は Console を確認してください。", "OK");
            }
            SetAvatar(_avatar);
            GUIUtility.ExitGUI();
        }

        private void DrawSummary()
        {
            var errors = _report.Count(SwapSeverity.Error);
            var warnings = _report.Count(SwapSeverity.Warning);
            var infos = _report.Count(SwapSeverity.Info);
            var counts =
                $"一致したオブジェクト {_report.matchedObjectCount} / 元 {_report.oldObjectCount} / 新 {_report.newObjectCount}";
            if (errors > 0)
            {
                EditorGUILayout.HelpBox(
                    $"失われるもの {errors} 件、注意 {warnings} 件。このまま差し替えると、下の「失われる」項目はアバターから外れます。\n{counts}",
                    MessageType.Error);
            }
            else if (warnings > 0)
            {
                EditorGUILayout.HelpBox(
                    $"注意 {warnings} 件。アバターに追加したものは引き継がれますが、見た目や動きが変わる可能性があります。\n{counts}",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox($"問題は見つかりませんでした。✔\n{counts}", MessageType.Info);
            }
            if (_avatar == null)
            {
                EditorGUILayout.HelpBox("アバターが未指定のため、FBX 同士の比較だけを行いました。", MessageType.None);
            }
        }

        private void DrawIssues()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _showInfo = GUILayout.Toggle(_showInfo,
                    $"参考情報も表示 ({_report.Count(SwapSeverity.Info)})", EditorStyles.toolbarButton);
                GUILayout.FlexibleSpace();
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            SwapIssueKind? lastKind = null;
            foreach (var issue in _report.issues)
            {
                if (!_showInfo && issue.severity == SwapSeverity.Info) continue;
                if (issue.kind != lastKind)
                {
                    EditorGUILayout.Space(4f);
                    EditorGUILayout.LabelField(KindLabel(issue.kind), EditorStyles.boldLabel);
                    lastKind = issue.kind;
                }
                DrawIssueRow(issue);
            }
            EditorGUILayout.EndScrollView();
        }

        private static void DrawIssueRow(SwapIssue issue)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(Icon(issue.severity), GUILayout.Width(20), GUILayout.Height(20));
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(string.IsNullOrEmpty(issue.path) ? "(ルート)" : issue.path,
                        EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField(issue.message, EditorStyles.wordWrappedMiniLabel);
                }
                if (issue.context != null &&
                    GUILayout.Button("選択", EditorStyles.miniButton, GUILayout.Width(44)))
                {
                    EditorGUIUtility.PingObject(issue.context);
                    Selection.activeObject = issue.context is Component c ? c.gameObject : issue.context;
                }
            }
        }

        private static GUIContent Icon(SwapSeverity s)
        {
            switch (s)
            {
                case SwapSeverity.Error: return EditorGUIUtility.IconContent("console.erroricon.sml");
                case SwapSeverity.Warning: return EditorGUIUtility.IconContent("console.warnicon.sml");
                default: return EditorGUIUtility.IconContent("console.infoicon.sml");
            }
        }

        private static readonly Dictionary<SwapIssueKind, string> KindLabels = new Dictionary<SwapIssueKind, string>
        {
            { SwapIssueKind.LostAddedObject, "失われる: 追加したオブジェクト" },
            { SwapIssueKind.LostAddedComponent, "失われる: 追加したコンポーネント" },
            { SwapIssueKind.BrokenReference, "失われる: 参照" },
            { SwapIssueKind.StaleOldReference, "元の FBX を参照し続けるもの" },
            { SwapIssueKind.MissingObject, "新しい FBX にないオブジェクト" },
            { SwapIssueKind.AmbiguousName, "対応が曖昧なオブジェクト" },
            { SwapIssueKind.ComponentMissing, "なくなるコンポーネント" },
            { SwapIssueKind.BlendShapeMissing, "なくなる BlendShape" },
            { SwapIssueKind.SubMeshCountChanged, "マテリアルスロット数の変化" },
            { SwapIssueKind.Humanoid, "Humanoid 設定の違い" },
            { SwapIssueKind.NewObject, "新しい FBX にだけあるオブジェクト" },
            { SwapIssueKind.BlendShapeAdded, "増える BlendShape" },
            { SwapIssueKind.MeshStatsChanged, "メッシュの変化" },
        };

        private static string KindLabel(SwapIssueKind kind) =>
            KindLabels.TryGetValue(kind, out var label) ? label : kind.ToString();
    }
}
