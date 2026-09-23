using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Xestrel.Detection;
using Xestrel.Isolation;
using Xestrel.Runtime;

namespace Xestrel.UI
{
    internal sealed class XestrelIsolationWindow : EditorWindow
    {
        private const float LabelWidth = 130f;
        private const float ThumbnailSize = 48f;
        private const float TextureFieldHeight = 48f;

        private const int TabMaterials = 0;
        private const int TabTextures = 1;
        private const int TabAnimators = 2;
        private const int TabIsolated = 3;
        private const int TabAdditions = 4;
        private const int TabPending = 5;

        private GameObject _avatar;
        private bool _lockAvatar;
        private int _tab;
        private readonly Vector2[] _scrolls = new Vector2[6];
        private string _search = string.Empty;
        private readonly HashSet<Material> _expanded = new HashSet<Material>();
        private RuntimeAnimatorController _pendingAnimator;

        // Everything a texture-property walk over the copy materials can tell us about
        // one texture: where it is used and whether it is already a per-avatar copy.
        private sealed class TextureEntry
        {
            public Texture texture;
            public bool isolated;
            public Texture original; // recorded original when `texture` is one of our copies
            public readonly List<string> uses = new List<string>();
        }

        // Caches rebuilt lazily on Layout when _cachesDirty; cheap enough for typical
        // avatars but not for every repaint.
        private bool _cachesDirty = true;
        private List<KeyValuePair<Material, int>> _pendingMaterials = new List<KeyValuePair<Material, int>>();
        private List<TextureEntry> _textures = new List<TextureEntry>();
        private readonly List<KeyValuePair<string, RuntimeAnimatorController>> _pendingLayers =
            new List<KeyValuePair<string, RuntimeAnimatorController>>();
        // Another avatar in the scene whose bindings share copy assets with ours —
        // almost always a duplicated already-isolated avatar. Edits would bleed.
        private XestrelMaterialIsolation _sharesCopiesWith;
        // Workspace manifests offered for recovery when the renderers point at copies
        // but the state component is gone (prefab revert, scene mishap).
        private readonly List<XestrelWorkspaceManifest> _recoverCandidates =
            new List<XestrelWorkspaceManifest>();
        private int _recoverIndex;
        // The avatar's prefab *asset* references Xestrel copies — overrides were applied
        // to the prefab, so every instance in every scene uses those copies.
        private bool _prefabAssetHasCopies;
        // Copies in the workspace folder that nothing references or tracks any more.
        private List<Object> _unusedCopies = new List<Object>();
        // What was added to the avatar relative to its base prefab (derived, never stored).
        private AvatarAdditionsReport _additionsReport = new AvatarAdditionsReport();
        private sealed class AdditionRow
        {
            public GameObject go;
            public string sourcePath; // prefab asset path, or null for scene-only objects
            public List<KeyValuePair<Material, int>> pending;
        }
        private readonly List<AdditionRow> _additionRows = new List<AdditionRow>();

        [MenuItem("Window/Xestrel/Asset Isolation")]
        public static void Open()
        {
            var w = GetWindow<XestrelIsolationWindow>();
            w.titleContent = new GUIContent("Xestrel");
            w.minSize = new Vector2(440, 340);
            w.TryAutoSelect();
            w.Show();
        }

        public static void OpenFor(GameObject avatar)
        {
            var w = GetWindow<XestrelIsolationWindow>();
            w.titleContent = new GUIContent("Xestrel");
            if (avatar != null) w._avatar = avatar;
            w._cachesDirty = true;
            w.Show();
        }

        public static void OpenFor(XestrelMaterialIsolation state) =>
            OpenFor(state != null ? state.gameObject : null);

        private void OnEnable()
        {
            titleContent = new GUIContent("Xestrel");
            EditorApplication.hierarchyChanged += MarkCachesDirty;
            EditorApplication.projectChanged += MarkCachesDirty;
            Undo.undoRedoPerformed += MarkCachesDirty;
            TryAutoSelect();
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkCachesDirty;
            EditorApplication.projectChanged -= MarkCachesDirty;
            Undo.undoRedoPerformed -= MarkCachesDirty;
        }

        private void OnFocus() => MarkCachesDirty();

        private void OnSelectionChange()
        {
            if (_lockAvatar) return;
            var go = Selection.activeGameObject;
            if (go == null) return;
            var root = AvatarRootRecogniser.ResolveAvatarRoot(go);
            // Only follow the selection onto things that look like an avatar; clicking
            // scenery should not steal the window away from the current target.
            if (root == null) return;
            if (!AvatarRootRecogniser.HasAvatarDescriptor(root) &&
                root.GetComponent<XestrelMaterialIsolation>() == null) return;
            if (root == _avatar) return;
            _avatar = root;
            _cachesDirty = true;
            Repaint();
        }

        private void MarkCachesDirty()
        {
            _cachesDirty = true;
            Repaint();
        }

        private void TryAutoSelect()
        {
            if (_avatar != null) return;
            var go = Selection.activeGameObject;
            if (go == null) return;
            _avatar = AvatarRootRecogniser.ResolveAvatarRoot(go) ?? go;
        }

        private XestrelMaterialIsolation CurrentState =>
            _avatar != null ? _avatar.GetComponent<XestrelMaterialIsolation>() : null;

        // ---------- caches ----------

        private void RebuildCaches()
        {
            WorkspaceManifests.HealWorkspaceName(CurrentState);
            _pendingMaterials = MaterialIsolator.CollectPendingMaterials(_avatar);
            RebuildTextureEntries();
            RebuildPendingLayers();
            DetectSharedCopies();
            RebuildRecoverCandidates();
            DetectPrefabAssetCopies();
            _unusedCopies = WorkspaceAudit.CollectUnusedCopies(CurrentState);
            RebuildAdditions();
        }

        private void RebuildAdditions()
        {
            _additionRows.Clear();
            _additionsReport = AvatarAdditions.Scan(_avatar);
            foreach (var go in _additionsReport.addedObjects)
            {
                if (go == null) continue;
                string sourcePath = null;
                if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
                {
                    var src = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (src != null) sourcePath = AssetDatabase.GetAssetPath(src);
                }
                _additionRows.Add(new AdditionRow
                {
                    go = go,
                    sourcePath = sourcePath,
                    pending = MaterialIsolator.CollectPendingMaterials(go),
                });
            }
        }

        private void RebuildRecoverCandidates()
        {
            _recoverCandidates.Clear();
            if (_avatar == null || CurrentState != null) return;
            var referenced = CollectReferencedWorkspaceFolders();
            // No renderer/descriptor slot points at a copy → this is a fresh avatar,
            // not a lost-component case; don't offer recovery.
            if (referenced.Count == 0) return;

            var all = WorkspaceManifests.FindAll();
            foreach (var m in all)
            {
                var folder = WorkspaceManifests.FolderNameOf(m) ?? m.avatarName;
                if (folder != null && referenced.Contains(folder)) _recoverCandidates.Add(m);
            }
            foreach (var m in all)
            {
                if (!_recoverCandidates.Contains(m)) _recoverCandidates.Add(m);
            }
            if (_recoverIndex >= _recoverCandidates.Count) _recoverIndex = 0;
        }

        private HashSet<string> CollectReferencedWorkspaceFolders()
        {
            var result = new HashSet<string>();
            foreach (var r in _avatar.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials) AddWorkspaceOf(m, result);
            }
            var desc = _avatar.GetComponent<VRCAvatarDescriptor>();
            if (desc != null)
            {
                if (desc.baseAnimationLayers != null)
                    foreach (var l in desc.baseAnimationLayers) AddWorkspaceOf(l.animatorController, result);
                if (desc.specialAnimationLayers != null)
                    foreach (var l in desc.specialAnimationLayers) AddWorkspaceOf(l.animatorController, result);
            }
            return result;
        }

        private static void AddWorkspaceOf(Object asset, HashSet<string> result)
        {
            if (asset == null) return;
            var path = AssetDatabase.GetAssetPath(asset);
            if (!IsolationPaths.IsUnderIsolationRoot(path)) return;
            var rest = path.Substring(IsolationPaths.Root.Length + 1);
            var slash = rest.IndexOf('/');
            if (slash > 0) result.Add(rest.Substring(0, slash));
        }

        private void DetectPrefabAssetCopies()
        {
            _prefabAssetHasCopies = false;
            if (_avatar == null || !PrefabUtility.IsPartOfPrefabInstance(_avatar)) return;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(_avatar);
            if (source == null) return;

            foreach (var r in source.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m != null && IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(m)))
                    {
                        _prefabAssetHasCopies = true;
                        return;
                    }
                }
            }
            var desc = source.GetComponent<VRCAvatarDescriptor>();
            if (desc == null) return;
            _prefabAssetHasCopies =
                AnyLayerUnderIsolationRoot(desc.baseAnimationLayers) ||
                AnyLayerUnderIsolationRoot(desc.specialAnimationLayers);
        }

        private static bool AnyLayerUnderIsolationRoot(VRCAvatarDescriptor.CustomAnimLayer[] layers)
        {
            if (layers == null) return false;
            foreach (var l in layers)
            {
                if (l.animatorController != null &&
                    IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(l.animatorController)))
                    return true;
            }
            return false;
        }

        private void DetectSharedCopies()
        {
            _sharesCopiesWith = null;
            var state = CurrentState;
            if (state == null || state.bindings == null || state.bindings.Count == 0) return;

            var myCopies = new HashSet<Material>();
            foreach (var b in state.bindings)
                if (b != null && b.copy != null) myCopies.Add(b.copy);
            if (myCopies.Count == 0) return;

            foreach (var other in FindObjectsByType<XestrelMaterialIsolation>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (other == state || other.bindings == null) continue;
                foreach (var b in other.bindings)
                {
                    if (b != null && b.copy != null && myCopies.Contains(b.copy))
                    {
                        _sharesCopiesWith = other;
                        return;
                    }
                }
            }
        }

        private void RebuildTextureEntries()
        {
            _textures = new List<TextureEntry>();
            var state = CurrentState;
            if (state == null || state.bindings == null) return;

            var byTexture = new Dictionary<Texture, TextureEntry>();
            foreach (var mb in state.bindings)
            {
                if (mb == null || mb.copy == null) continue;
                var shader = mb.copy.shader;
                if (shader == null) continue;
                int count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    if (ShaderUtil.IsShaderPropertyHidden(shader, i)) continue;
                    var propName = ShaderUtil.GetPropertyName(shader, i);
                    var tex = mb.copy.GetTexture(propName);
                    if (tex == null) continue;

                    if (!byTexture.TryGetValue(tex, out var entry))
                    {
                        entry = new TextureEntry
                        {
                            texture = tex,
                            isolated = IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(tex)),
                            original = TextureIsolator.FindOriginal(state, tex),
                        };
                        byTexture[tex] = entry;
                        _textures.Add(entry);
                    }
                    entry.uses.Add(mb.copy.name + " › " + ShaderUtil.GetPropertyDescription(shader, i));
                }
            }
        }

        private void RebuildPendingLayers()
        {
            _pendingLayers.Clear();
            var desc = _avatar != null ? _avatar.GetComponent<VRCAvatarDescriptor>() : null;
            if (desc == null) return;
            CollectPendingLayers(desc.baseAnimationLayers);
            CollectPendingLayers(desc.specialAnimationLayers);
        }

        private void CollectPendingLayers(VRCAvatarDescriptor.CustomAnimLayer[] layers)
        {
            if (layers == null) return;
            foreach (var layer in layers)
            {
                var ac = layer.animatorController;
                if (ac == null) continue;
                if (IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(ac))) continue;
                _pendingLayers.Add(new KeyValuePair<string, RuntimeAnimatorController>(layer.type.ToString(), ac));
            }
        }

        private int PendingTextureCount()
        {
            int n = 0;
            foreach (var e in _textures)
                if (!e.isolated) n++;
            return n;
        }

        // ---------- OnGUI ----------

        private void OnGUI()
        {
            if (Event.current.type == EventType.Layout && _cachesDirty)
            {
                RebuildCaches();
                _cachesDirty = false;
            }

            DrawTitleBar();
            DrawAvatarSection();
            if (_avatar == null) return;

            EditorGUILayout.Space(2f);
            var pendingTotal = _pendingMaterials.Count + PendingTextureCount() + _pendingLayers.Count;
            var tabs = new[]
            {
                new GUIContent("マテリアル", "専用コピーにしたマテリアルと、その中のテクスチャ"),
                new GUIContent("テクスチャ", "専用マテリアルが使っているテクスチャの一覧"),
                new GUIContent("アニメーター", "Playable Layer の AnimatorController"),
                new GUIContent("専用化済み", "このアバター用にコピー済みのアセット一覧"),
                new GUIContent("追加物", "ベース Prefab に対して後から追加したオブジェクト / コンポーネント"),
                new GUIContent(pendingTotal > 0 ? $"未専用化 ({pendingTotal})" : "未専用化",
                    "まだ他のアバターと共有しているアセット"),
            };
            _tab = GUILayout.Toolbar(_tab, tabs, GUILayout.Height(22f));
            EditorGUILayout.Space(2f);

            switch (_tab)
            {
                case TabMaterials: DrawMaterialsTab(); break;
                case TabTextures: DrawTexturesTab(); break;
                case TabAnimators: DrawAnimatorsTab(); break;
                case TabIsolated: DrawIsolatedTab(); break;
                case TabAdditions: DrawAdditionsTab(); break;
                case TabPending: DrawPendingTab(); break;
            }
        }

        // ---------- header ----------

        private void DrawTitleBar()
        {
            var rect = EditorGUILayout.GetControlRect(false, 28f);
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.18f));
            var labelRect = new Rect(rect.x + 10f, rect.y + 6f, rect.width - 90f, rect.height - 8f);
            GUI.Label(labelRect, "アセット専用化", EditorStyles.boldLabel);
            var btnRect = new Rect(rect.xMax - 70f, rect.y + 4f, 60f, rect.height - 8f);
            using (new EditorGUI.DisabledScope(_avatar == null))
            {
                if (GUI.Button(btnRect, new GUIContent("依存関係",
                        "このアバターが参照しているアセットをツリー表示します"), EditorStyles.miniButton))
                {
                    XestrelDependencyWindow.OpenFor(_avatar);
                }
            }
        }

        private void DrawAvatarSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new LabelWidthScope(60f))
                    {
                        var next = (GameObject)EditorGUILayout.ObjectField("アバター", _avatar, typeof(GameObject), true);
                        if (next != _avatar)
                        {
                            _avatar = next;
                            _cachesDirty = true;
                        }
                    }
                    _lockAvatar = GUILayout.Toggle(
                        _lockAvatar,
                        new GUIContent("", "ロック: Hierarchy の選択を変えても、このアバターを表示し続けます"),
                        "IN LockButton",
                        GUILayout.Width(16f), GUILayout.Height(16f));
                }

                EditorGUILayout.Space(4f);

                using (new EditorGUILayout.HorizontalScope())
                using (new EditorGUI.DisabledScope(_avatar == null))
                {
                    var isolateLabel = _pendingMaterials.Count > 0 ? $"専用コピーを作成 ({_pendingMaterials.Count})" : "専用コピーを作成";
                    if (GUILayout.Button(new GUIContent(isolateLabel,
                            "このアバターが使っている共有マテリアルを Assets/Xestrel/<アバター名>/Materials/ にコピーし、" +
                            "レンダラーの参照をコピーに差し替えます。元のマテリアルは変更しません。"),
                        GUILayout.Height(24)))
                    {
                        Isolator.Isolate(_avatar);
                        _cachesDirty = true;
                    }
                    using (new EditorGUI.DisabledScope(CurrentState == null))
                    {
                        if (GUILayout.Button(new GUIContent("すべて元に戻す",
                                "マテリアル / テクスチャ / アニメーターの参照を、コピー前の共有アセットに戻します（コピーはディスクに残ります）"),
                            GUILayout.Height(24)) && ConfirmRestoreAll())
                        {
                            Isolator.Restore(CurrentState);
                            _cachesDirty = true;
                        }
                        using (new EditorGUI.DisabledScope(!HasForkableBindings(CurrentState)))
                        {
                            if (GUILayout.Button(new GUIContent("複製して独立",
                                    "このアバターをシーン内に複製し、複製側に別の専用コピーを持たせます（ここまでの編集は引き継がれます）"),
                                GUILayout.Height(24), GUILayout.Width(90)) && ConfirmDuplicateFork())
                            {
                                var dup = WorkspaceForker.DuplicateAndFork(CurrentState);
                                if (dup != null)
                                {
                                    Selection.activeGameObject = dup;
                                    if (!_lockAvatar) _avatar = dup;
                                }
                                _cachesDirty = true;
                                GUIUtility.ExitGUI();
                            }
                        }
                        if (GUILayout.Button(new GUIContent("フォルダ",
                                "このアバターのコピー置き場を Project ウィンドウで表示します"),
                            GUILayout.Height(24), GUILayout.Width(60)))
                        {
                            PingAvatarFolder();
                        }
                    }
                }

                EditorGUILayout.Space(2f);
                DrawStatus();
            }
        }

        private static bool HasForkableBindings(XestrelMaterialIsolation state) =>
            state != null &&
            ((state.bindings?.Count ?? 0) > 0 || (state.animatorBindings?.Count ?? 0) > 0);

        private bool ConfirmDuplicateFork()
        {
            return EditorUtility.DisplayDialog(
                "Xestrel — 複製して独立",
                $"「{_avatar.name}」を複製し、複製側に専用コピーを新しく作りますか？\n" +
                "（Assets/Xestrel/ 以下に新しいフォルダが作られます）\n\n" +
                "ここまでの編集内容は複製側に引き継がれます。元のアバターとそのコピーは変更しません。",
                "複製する", "キャンセル");
        }

        private bool ConfirmRestoreAll()
        {
            var state = CurrentState;
            var name = state != null ? state.avatarName : "アバター";
            return EditorUtility.DisplayDialog(
                "Xestrel — すべて元に戻す",
                $"「{name}」の参照を、コピー前の共有アセットに戻しますか？\n\n" +
                "マテリアル / テクスチャ / アニメーターの対応記録はすべて消去されます。" +
                "Assets/Xestrel/ 以下のコピーファイルは削除されずに残ります。",
                "元に戻す", "キャンセル");
        }

        private void PingAvatarFolder()
        {
            var state = CurrentState;
            if (state == null) return;
            var dir = IsolationPaths.AvatarDir(state.avatarName);
            var folder = AssetDatabase.LoadAssetAtPath<Object>(dir);
            if (folder == null)
            {
                EditorUtility.DisplayDialog("Xestrel", $"コピー用フォルダはまだありません:\n{dir}", "OK");
                return;
            }
            EditorGUIUtility.PingObject(folder);
            Selection.activeObject = folder;
        }

        private void DrawStatus()
        {
            if (_avatar == null)
            {
                EditorGUILayout.HelpBox("シーン内のアバターを選択してください。", MessageType.Info);
                return;
            }
            if (_prefabAssetHasCopies)
            {
                EditorGUILayout.HelpBox(
                    "このアバターの Prefab アセット自体が Xestrel のコピーを参照しています（Renderer / Descriptor の " +
                    "オーバーライドが Prefab に Apply された状態です）。全シーンのこの Prefab のインスタンスが " +
                    "同じコピーを使います。意図していなければ Prefab アセット側のオーバーライドを戻してください。",
                    MessageType.Warning);
            }

            var state = CurrentState;
            if (state == null)
            {
                if (DrawRecoverSection()) return;
                var msg = _pendingMaterials.Count > 0
                    ? $"まだ専用化されていません。「専用コピーを作成」で共有マテリアル {_pendingMaterials.Count} 個をこのアバター専用にコピーします。"
                    : "まだ専用化されていません。「専用コピーを作成」でこのアバターのマテリアルを専用にコピーします。";
                msg += "\n\n専用化 = 他のアバターと共有しているアセットをコピーし、このアバターだけがコピーを使うよう参照を差し替えること。" +
                       "コピーを編集しても他のアバターには影響しません。元のアセットは変更されません。";
                EditorGUILayout.HelpBox(msg, MessageType.Info);
                return;
            }

            if (_sharesCopiesWith != null)
            {
                var otherName = _sharesCopiesWith.gameObject != null ? _sharesCopiesWith.gameObject.name : "?";
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox(
                        $"このアバターは「{otherName}」と同じコピーを共有しています（専用化済みアバターを複製した？）。" +
                        "このままだと編集が両方に反映されます。「コピーを分ける」で、このアバター用に別のコピーを作り直せます" +
                        "（ここまでの編集は引き継がれ、相手のアバターは変更しません）。",
                        MessageType.Warning);
                    if (GUILayout.Button(new GUIContent("コピーを\n分ける",
                            "専用マテリアル / テクスチャ / アニメーターを新しいフォルダにもう一度コピーし、このアバターだけをそちらに付け替えます"),
                        GUILayout.Width(72), GUILayout.Height(52)))
                    {
                        if (EditorUtility.DisplayDialog(
                                "Xestrel — コピーを分ける",
                                $"「{_avatar.name}」用に、Assets/Xestrel/ 以下の新しいフォルダへ別のコピーを作りますか？\n\n" +
                                $"ここまでの編集は引き継がれます。「{otherName}」は今のコピーを使い続け、変更されません。",
                                "分ける", "キャンセル"))
                        {
                            WorkspaceForker.Fork(state);
                            _cachesDirty = true;
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }

            if (Isolator.HasDeadBindings(state))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox(
                        "削除されたアセットを指している対応記録があります。", MessageType.Warning);
                    if (GUILayout.Button(new GUIContent("記録を\n掃除", "削除済みアセットを指す対応記録を取り除きます"),
                            GUILayout.Width(60), GUILayout.Height(38)))
                    {
                        Isolator.PruneDeadBindings(state);
                        _cachesDirty = true;
                    }
                }
            }

            var matCount = state.bindings?.Count ?? 0;
            var texCount = state.textureBindings?.Count ?? 0;
            var animCount = state.animatorBindings?.Count ?? 0;
            var clipCount = state.clipBindings?.Count ?? 0;
            if (matCount == 0 && animCount == 0)
            {
                EditorGUILayout.HelpBox("専用コピーの記録がありません。「専用コピーを作成」を押してください。", MessageType.Info);
                return;
            }
            var pendingNote = _pendingMaterials.Count > 0
                ? $" — 未専用化のマテリアルが {_pendingMaterials.Count} 個あります"
                : "";
            var renamedNote = _avatar.name != state.avatarName
                ? $"\nGameObject 名は「{_avatar.name}」に変わっていますが、コピーは引き続き Assets/Xestrel/{state.avatarName}/ に置かれます。"
                : "";
            EditorGUILayout.HelpBox(
                $"「{state.avatarName}」の専用コピー: マテリアル {matCount} / テクスチャ {texCount} / アニメーター {animCount} / クリップ {clipCount}{pendingNote}{renamedNote}",
                _pendingMaterials.Count > 0 ? MessageType.Warning : MessageType.None);
        }

        // Offer to rebuild the lost state component from a workspace manifest. Returns
        // true when the recover UI was drawn (the caller skips the "not isolated" hint —
        // the renderers demonstrably point at copies already).
        private bool DrawRecoverSection()
        {
            if (_recoverCandidates.Count == 0) return false;

            EditorGUILayout.HelpBox(
                "このアバターのレンダラーは Xestrel のコピーを参照していますが、Xestrel コンポーネントが見つかりません" +
                "（Prefab の Revert など）。「復旧」でフォルダ内の記録ファイルから対応関係を再構築できます。",
                MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                var labels = new string[_recoverCandidates.Count];
                for (int i = 0; i < labels.Length; i++)
                {
                    var m = _recoverCandidates[i];
                    labels[i] = m != null
                        ? (WorkspaceManifests.FolderNameOf(m) ?? m.avatarName ?? "(名前なし)")
                        : "(見つかりません)";
                }
                _recoverIndex = EditorGUILayout.Popup(_recoverIndex, labels);
                if (GUILayout.Button(new GUIContent("復旧",
                        "選んだフォルダの記録ファイルをもとに Xestrel コンポーネントを付け直します"),
                    GUILayout.Width(80)))
                {
                    if (_recoverIndex >= 0 && _recoverIndex < _recoverCandidates.Count)
                    {
                        WorkspaceManifests.Recover(_avatar, _recoverCandidates[_recoverIndex]);
                        _cachesDirty = true;
                    }
                }
            }
            return true;
        }

        // ---------- Materials tab ----------

        private void DrawMaterialsTab()
        {
            var state = CurrentState;
            if (state == null || state.bindings == null || state.bindings.Count == 0)
            {
                EditorGUILayout.HelpBox("専用コピーにしたマテリアルはまだありません。「専用コピーを作成」を押してください。", MessageType.Info);
                return;
            }

            DrawSearchToolbar(state);

            _scrolls[TabMaterials] = EditorGUILayout.BeginScrollView(_scrolls[TabMaterials]);
            var filter = (_search ?? string.Empty).Trim();
            int shown = 0;
            // Snapshot so per-row Restore (which removes the binding) can't break enumeration.
            foreach (var b in new List<XestrelMaterialBinding>(state.bindings))
            {
                if (b == null || b.copy == null) continue;
                if (filter.Length > 0 && !MatchesFilter(b, filter)) continue;
                DrawBinding(state, b);
                shown++;
            }
            if (shown == 0)
            {
                EditorGUILayout.HelpBox("検索に一致するマテリアルはありません。", MessageType.None);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawSearchToolbar(XestrelMaterialIsolation state)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _search = GUILayout.TextField(_search ?? string.Empty, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));
                if (state != null)
                {
                    if (GUILayout.Button("すべて開く", EditorStyles.toolbarButton, GUILayout.Width(70)))
                    {
                        foreach (var b in state.bindings)
                            if (b != null && b.copy != null) _expanded.Add(b.copy);
                    }
                    if (GUILayout.Button("すべて閉じる", EditorStyles.toolbarButton, GUILayout.Width(76)))
                    {
                        _expanded.Clear();
                    }
                }
            }
        }

        private static bool MatchesFilter(XestrelMaterialBinding b, string filter)
        {
            if (b.original != null && b.original.name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (b.copy != null && b.copy.name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private void DrawBinding(XestrelMaterialIsolation state, XestrelMaterialBinding b)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawBindingHeader(b);
                if (!_expanded.Contains(b.copy)) return;

                EditorGUILayout.Space(4f);
                DrawAssetRows(b);
                EditorGUILayout.Space(4f);
                DrawBindingActions(state, b);
                EditorGUILayout.Space(6f);
                DrawSectionLabel("テクスチャ");
                DrawTextureProperties(b.copy);
            }
        }

        private void DrawBindingActions(XestrelMaterialIsolation state, XestrelMaterialBinding b)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("テクスチャを全部専用化",
                        "このマテリアルが使っている共有テクスチャをすべてコピーして差し替えます"),
                    EditorStyles.miniButton, GUILayout.Width(140)))
                {
                    TextureIsolator.IsolateAllProperties(state, b.copy);
                    _cachesDirty = true;
                }
                if (GUILayout.Button(new GUIContent("マテリアルを元に戻す",
                        "レンダラーの参照を元の共有マテリアルに戻します（コピーはディスクに残ります）"),
                    EditorStyles.miniButton, GUILayout.Width(130)))
                {
                    var name = b.original != null ? b.original.name : b.copy.name;
                    if (EditorUtility.DisplayDialog(
                            "Xestrel — マテリアルを元に戻す",
                            $"レンダラーの参照を元の「{name}」に戻しますか？\n\n" +
                            "コピーファイルは削除されずに残ります。",
                            "元に戻す", "キャンセル"))
                    {
                        _expanded.Remove(b.copy);
                        MaterialIsolator.RestoreBinding(state, b);
                        _cachesDirty = true;
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        private void DrawBindingHeader(XestrelMaterialBinding b)
        {
            var rect = EditorGUILayout.GetControlRect(false, 20f);
            var foldRect = new Rect(rect.x, rect.y, rect.width - 64f, rect.height);
            var pingRect = new Rect(rect.xMax - 60f, rect.y + 1f, 60f, rect.height - 2f);

            var expanded = _expanded.Contains(b.copy);
            var label = b.original != null ? b.original.name : b.copy.name;
            var newExpanded = EditorGUI.Foldout(foldRect, expanded, label, true, EditorStyles.foldoutHeader);
            if (newExpanded != expanded)
            {
                if (newExpanded) _expanded.Add(b.copy); else _expanded.Remove(b.copy);
            }

            if (GUI.Button(pingRect, "選択", EditorStyles.miniButton))
            {
                EditorGUIUtility.PingObject(b.copy);
                Selection.activeObject = b.copy;
            }
        }

        private static void DrawAssetRows(XestrelMaterialBinding b)
        {
            using (new LabelWidthScope(LabelWidth))
            using (new EditorGUI.IndentLevelScope())
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("元 (共有)", b.original, typeof(Material), false);
                }
                EditorGUILayout.ObjectField("コピー (編集用)", b.copy, typeof(Material), false);
            }
        }

        private static void DrawSectionLabel(string text)
        {
            var rect = EditorGUILayout.GetControlRect(false, 18f);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + rect.height - 1f, rect.width, 1f), new Color(0f, 0f, 0f, 0.2f));
            GUI.Label(new Rect(rect.x, rect.y, rect.width, rect.height - 1f), text, EditorStyles.miniBoldLabel);
        }

        private void DrawTextureProperties(Material copy)
        {
            var shader = copy != null ? copy.shader : null;
            if (shader == null)
            {
                EditorGUILayout.HelpBox("マテリアルにシェーダーがありません。", MessageType.Warning);
                return;
            }

            int count = ShaderUtil.GetPropertyCount(shader);
            int drawn = 0;
            using (new LabelWidthScope(LabelWidth))
            using (new EditorGUI.IndentLevelScope())
            {
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    if (ShaderUtil.IsShaderPropertyHidden(shader, i)) continue;

                    var propName = ShaderUtil.GetPropertyName(shader, i);
                    var description = ShaderUtil.GetPropertyDescription(shader, i);
                    DrawTextureRow(copy, propName, description);
                    drawn++;
                }
            }
            if (drawn == 0)
            {
                EditorGUILayout.LabelField("(表示できるテクスチャ項目なし)", EditorStyles.miniLabel);
            }
        }

        private void DrawTextureRow(Material copy, string propName, string description)
        {
            const float ButtonWidth = 64f;
            const float ButtonGap = 4f;

            var rowRect = EditorGUILayout.GetControlRect(false, TextureFieldHeight);
            var indent = EditorGUI.indentLevel * 15f;
            rowRect.x += indent;
            rowRect.width -= indent;

            var thumbRect = new Rect(rowRect.x, rowRect.y, ThumbnailSize, ThumbnailSize);
            var current = copy.GetTexture(propName);
            DrawThumbnail(thumbRect, current);

            var fieldX = thumbRect.xMax + 6f;
            var contentRight = rowRect.xMax - ButtonWidth - ButtonGap;
            var fieldWidth = contentRight - fieldX;
            var labelRect = new Rect(fieldX, rowRect.y, fieldWidth, EditorGUIUtility.singleLineHeight);
            var fieldRect = new Rect(fieldX, rowRect.y + EditorGUIUtility.singleLineHeight + 2f,
                                     fieldWidth, EditorGUIUtility.singleLineHeight);

            var isolated = current != null &&
                           IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(current));
            var labelContent = new GUIContent(
                isolated ? description + "  ●" : description,
                isolated ? propName + " (専用コピー)" : propName);
            GUI.Label(labelRect, labelContent, EditorStyles.label);

            var prevIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            var next = (Texture)EditorGUI.ObjectField(fieldRect, current, typeof(Texture), false);
            EditorGUI.indentLevel = prevIndent;

            if (!ReferenceEquals(next, current))
            {
                Undo.RecordObject(copy, "Xestrel Set Texture");
                copy.SetTexture(propName, next);
                EditorUtility.SetDirty(copy);
                current = next;
                isolated = current != null &&
                           IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(current));
                _cachesDirty = true;
            }

            var buttonRect = new Rect(contentRight + ButtonGap, rowRect.y + EditorGUIUtility.singleLineHeight + 2f,
                                      ButtonWidth, EditorGUIUtility.singleLineHeight);
            var state = CurrentState;
            if (isolated && TextureIsolator.FindOriginal(state, current) != null)
            {
                if (GUI.Button(buttonRect, new GUIContent("元に戻す",
                        "このスロットを元の共有テクスチャに戻します（コピーはディスクに残ります）"),
                    EditorStyles.miniButton))
                {
                    TextureIsolator.RestoreProperty(state, copy, propName);
                    _cachesDirty = true;
                }
            }
            else
            {
                using (new EditorGUI.DisabledScope(current == null || isolated || state == null))
                {
                    var btnLabel = current == null ? "—" : (isolated ? "専用化済" : "専用化");
                    if (GUI.Button(buttonRect, new GUIContent(btnLabel,
                            "このテクスチャをコピーして、このスロットだけコピーに差し替えます"), EditorStyles.miniButton))
                    {
                        TextureIsolator.IsolateProperty(state, copy, propName);
                        _cachesDirty = true;
                    }
                }
            }
        }

        private static void DrawThumbnail(Rect rect, Texture texture)
        {
            if (texture != null)
            {
                var preview = AssetPreview.GetAssetPreview(texture);
                if (preview == null) preview = AssetPreview.GetMiniThumbnail(texture);
                if (preview != null) GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit);
                else GUI.Box(rect, GUIContent.none);
            }
            else
            {
                GUI.Box(rect, "none", EditorStyles.helpBox);
            }
        }

        // ---------- Textures tab ----------

        private void DrawTexturesTab()
        {
            var state = CurrentState;
            if (state == null || state.bindings == null || state.bindings.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "専用コピーにしたマテリアルがまだありません。テクスチャは専用マテリアルから一覧するため、先に「専用コピーを作成」を押してください。",
                    MessageType.Info);
                return;
            }
            if (_textures.Count == 0)
            {
                EditorGUILayout.HelpBox("専用マテリアルはテクスチャを参照していません。", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _search = GUILayout.TextField(_search ?? string.Empty, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));
                var isolatedCount = _textures.Count - PendingTextureCount();
                GUILayout.Label($"{isolatedCount}/{_textures.Count} 専用化済", EditorStyles.miniLabel, GUILayout.Width(100));
            }

            _scrolls[TabTextures] = EditorGUILayout.BeginScrollView(_scrolls[TabTextures]);
            var filter = (_search ?? string.Empty).Trim();
            int shown = 0;
            foreach (var entry in _textures)
            {
                if (entry.texture == null) continue;
                if (filter.Length > 0 && !MatchesTextureFilter(entry, filter)) continue;
                DrawTextureListRow(state, entry);
                shown++;
            }
            if (shown == 0)
            {
                EditorGUILayout.HelpBox("検索に一致するテクスチャはありません。", MessageType.None);
            }
            EditorGUILayout.EndScrollView();
        }

        private static bool MatchesTextureFilter(TextureEntry entry, string filter)
        {
            if (entry.texture.name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var use in entry.uses)
            {
                if (use.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private void DrawTextureListRow(XestrelMaterialIsolation state, TextureEntry entry)
        {
            const float ButtonWidth = 64f;
            const float SelectWidth = 56f;
            const float Gap = 4f;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var rowRect = EditorGUILayout.GetControlRect(false, TextureFieldHeight);
                var thumbRect = new Rect(rowRect.x, rowRect.y, ThumbnailSize, ThumbnailSize);
                DrawThumbnail(thumbRect, entry.texture);

                var textX = thumbRect.xMax + 6f;
                var buttonsWidth = ButtonWidth + SelectWidth + Gap * 2f;
                var textWidth = rowRect.xMax - buttonsWidth - textX;
                var nameRect = new Rect(textX, rowRect.y + 2f, textWidth, EditorGUIUtility.singleLineHeight);
                var usesRect = new Rect(textX, rowRect.y + EditorGUIUtility.singleLineHeight + 4f,
                                        textWidth, EditorGUIUtility.singleLineHeight);

                var nameLabel = entry.isolated ? entry.texture.name + "  ●" : entry.texture.name;
                GUI.Label(nameRect, new GUIContent(nameLabel,
                    entry.isolated ? "このアバター専用のコピー" : "共有テクスチャ（他のアバターと共用）"), EditorStyles.boldLabel);

                var usesSummary = entry.uses.Count == 1
                    ? entry.uses[0]
                    : $"{entry.uses.Count} スロット: " + string.Join(", ", entry.uses);
                GUI.Label(usesRect, new GUIContent(Truncate(usesSummary, 120), string.Join("\n", entry.uses)),
                    EditorStyles.miniLabel);

                var buttonY = rowRect.y + (TextureFieldHeight - EditorGUIUtility.singleLineHeight) * 0.5f;
                var selectRect = new Rect(rowRect.xMax - buttonsWidth + Gap, buttonY, SelectWidth, EditorGUIUtility.singleLineHeight);
                var actionRect = new Rect(rowRect.xMax - ButtonWidth, buttonY, ButtonWidth, EditorGUIUtility.singleLineHeight);

                if (GUI.Button(selectRect, "選択", EditorStyles.miniButton))
                {
                    EditorGUIUtility.PingObject(entry.texture);
                    Selection.activeObject = entry.texture;
                }

                if (!entry.isolated)
                {
                    if (GUI.Button(actionRect, new GUIContent("専用化",
                            "このテクスチャをコピーし、使っている全スロットをコピーに差し替えます"),
                        EditorStyles.miniButton))
                    {
                        TextureIsolator.IsolateTextureAcrossMaterials(state, entry.texture);
                        _cachesDirty = true;
                    }
                }
                else if (entry.original != null)
                {
                    if (GUI.Button(actionRect, new GUIContent("元に戻す",
                            "このコピーを使っている全スロットを元の共有テクスチャに戻します"),
                        EditorStyles.miniButton))
                    {
                        TextureIsolator.RestoreTextureAcrossMaterials(state, entry.texture);
                        _cachesDirty = true;
                    }
                }
                else
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        GUI.Button(actionRect, "専用化済", EditorStyles.miniButton);
                    }
                }
            }
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        // ---------- Animators tab ----------

        private void DrawAnimatorsTab()
        {
            var state = CurrentState;

            if (_pendingLayers.Count > 0)
            {
                DrawSectionLabel("Playable Layer（共有のまま）");
                DrawPendingLayerRows(state, string.Empty);
                EditorGUILayout.Space(4f);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _pendingAnimator = (RuntimeAnimatorController)EditorGUILayout.ObjectField(
                    _pendingAnimator, typeof(RuntimeAnimatorController), false);

                using (new EditorGUI.DisabledScope(_pendingAnimator == null || state == null))
                {
                    if (GUILayout.Button(new GUIContent("専用化",
                            "指定したコントローラーと使用クリップをこのアバター用にコピーします"), GUILayout.Width(80)))
                    {
                        AnimatorIsolator.IsolateController(state, _pendingAnimator);
                        _pendingAnimator = null;
                        _cachesDirty = true;
                    }
                }
            }

            if (state == null)
            {
                EditorGUILayout.HelpBox("先に「専用コピーを作成」でマテリアルを専用化してください（管理用コンポーネントが作られます）。", MessageType.None);
                return;
            }

            if (state.animatorBindings == null || state.animatorBindings.Count == 0)
            {
                EditorGUILayout.LabelField("(専用化したアニメーターなし)", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.Space(2f);
            DrawSectionLabel("専用化済みのコントローラー");
            _scrolls[TabAnimators] = EditorGUILayout.BeginScrollView(_scrolls[TabAnimators]);
            foreach (var b in state.animatorBindings)
            {
                if (b == null || b.copy == null) continue;
                DrawAnimatorBindingRow(b);
            }
            EditorGUILayout.EndScrollView();
        }

        private int DrawPendingLayerRows(XestrelMaterialIsolation state, string filter)
        {
            int shown = 0;
            foreach (var layer in _pendingLayers)
            {
                if (filter.Length > 0 &&
                    layer.Key.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    (layer.Value == null ||
                     layer.Value.name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0)) continue;
                shown++;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(layer.Key, GUILayout.Width(80));
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(layer.Value, typeof(RuntimeAnimatorController), false);
                    }
                    using (new EditorGUI.DisabledScope(state == null))
                    {
                        if (GUILayout.Button(new GUIContent("専用化",
                                "この Playable Layer のコントローラー（と使用クリップ）をコピーし、Descriptor の参照を差し替えます"),
                            GUILayout.Width(80)))
                        {
                            AnimatorIsolator.IsolateController(state, layer.Value);
                            _cachesDirty = true;
                        }
                    }
                }
            }
            return shown;
        }

        private static void DrawAnimatorBindingRow(XestrelAnimatorBinding b)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var label = b.original != null ? b.original.name : "<見つかりません>";
                EditorGUILayout.LabelField(label, GUILayout.MinWidth(80));
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(b.copy, typeof(RuntimeAnimatorController), false);
                }
                if (GUILayout.Button("選択", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    EditorGUIUtility.PingObject(b.copy);
                    Selection.activeObject = b.copy;
                }
            }
        }

        // ---------- Isolated tab ----------

        private void DrawIsolatedTab()
        {
            var state = CurrentState;
            if (state == null)
            {
                EditorGUILayout.HelpBox("このアバターにはまだ専用コピーがありません。", MessageType.Info);
                return;
            }

            _scrolls[TabIsolated] = EditorGUILayout.BeginScrollView(_scrolls[TabIsolated]);

            var matBindings = state.bindings ?? new List<XestrelMaterialBinding>();
            DrawSectionLabel($"マテリアル ({matBindings.Count})");
            if (matBindings.Count == 0)
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            // Snapshot so per-row Restore can't break enumeration.
            foreach (var b in new List<XestrelMaterialBinding>(matBindings))
            {
                if (b == null || b.copy == null) continue;
                DrawIsolatedMaterialRow(state, b);
            }

            EditorGUILayout.Space(6f);
            int isolatedTexCount = _textures.Count - PendingTextureCount();
            DrawSectionLabel($"テクスチャ ({isolatedTexCount})");
            if (isolatedTexCount == 0)
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            foreach (var entry in _textures)
            {
                if (entry.texture == null || !entry.isolated) continue;
                DrawTextureListRow(state, entry);
            }

            EditorGUILayout.Space(6f);
            var animBindings = state.animatorBindings ?? new List<XestrelAnimatorBinding>();
            DrawSectionLabel($"アニメーター ({animBindings.Count})");
            if (animBindings.Count == 0)
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            foreach (var b in animBindings)
            {
                if (b == null || b.copy == null) continue;
                DrawAnimatorBindingRow(b);
            }

            EditorGUILayout.Space(6f);
            var clipBindings = state.clipBindings ?? new List<XestrelClipBinding>();
            DrawSectionLabel($"アニメーションクリップ ({clipBindings.Count})");
            if (clipBindings.Count == 0)
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            foreach (var b in clipBindings)
            {
                if (b == null || b.copy == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(b.original != null ? b.original.name : "<見つかりません>",
                        GUILayout.MinWidth(80));
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(b.copy, typeof(AnimationClip), false);
                    }
                    if (GUILayout.Button("選択", EditorStyles.miniButton, GUILayout.Width(60)))
                    {
                        EditorGUIUtility.PingObject(b.copy);
                        Selection.activeObject = b.copy;
                    }
                }
            }

            EditorGUILayout.Space(6f);
            DrawSectionLabel($"使われていないコピー ({_unusedCopies.Count})");
            if (_unusedCopies.Count == 0)
            {
                EditorGUILayout.LabelField("(なし — フォルダ内のコピーはすべて使用中)",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "このアバターのコピー用フォルダにありますが、アバターからは参照されておらず、対応記録もないファイルです" +
                    "（「元に戻す」後の残りや手動編集によるもの）。不要なら手動で削除して問題ありません。",
                    MessageType.Info);
                foreach (var asset in _unusedCopies)
                {
                    if (asset == null) continue;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUILayout.ObjectField(asset, typeof(Object), false);
                        }
                        if (GUILayout.Button("選択", EditorStyles.miniButton, GUILayout.Width(60)))
                        {
                            EditorGUIUtility.PingObject(asset);
                            Selection.activeObject = asset;
                        }
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawIsolatedMaterialRow(XestrelMaterialIsolation state, XestrelMaterialBinding b)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(b.original, typeof(Material), false);
                    EditorGUILayout.ObjectField(b.copy, typeof(Material), false);
                }
                if (GUILayout.Button("選択", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    EditorGUIUtility.PingObject(b.copy);
                    Selection.activeObject = b.copy;
                }
                if (GUILayout.Button(new GUIContent("元に戻す", "レンダラーの参照を元の共有マテリアルに戻します"),
                        EditorStyles.miniButton, GUILayout.Width(64)))
                {
                    var name = b.original != null ? b.original.name : b.copy.name;
                    if (EditorUtility.DisplayDialog(
                            "Xestrel — マテリアルを元に戻す",
                            $"レンダラーの参照を元の「{name}」に戻しますか？\n\nコピーファイルは削除されずに残ります。",
                            "元に戻す", "キャンセル"))
                    {
                        _expanded.Remove(b.copy);
                        MaterialIsolator.RestoreBinding(state, b);
                        _cachesDirty = true;
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        // ---------- Additions tab ----------

        private void DrawAdditionsTab()
        {
            _scrolls[TabAdditions] = EditorGUILayout.BeginScrollView(_scrolls[TabAdditions]);

            if (_additionsReport.isPrefabInstance)
            {
                DrawSectionLabel("ベース Prefab");
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(_additionsReport.basePrefab, typeof(GameObject), false);
                    }
                    if (GUILayout.Button("選択", EditorStyles.miniButton, GUILayout.Width(60)))
                    {
                        EditorGUIUtility.PingObject(_additionsReport.basePrefab);
                        Selection.activeObject = _additionsReport.basePrefab;
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "このアバターは Prefab インスタンスではない（Unpack 済み？）ため、ベース Prefab との差分は取れません。" +
                    "子の Prefab インスタンスだけを表示しています。",
                    MessageType.Info);
            }

            EditorGUILayout.Space(4f);
            DrawSectionLabel($"追加されたオブジェクト ({_additionRows.Count})");
            if (_additionRows.Count == 0)
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            foreach (var row in _additionRows)
            {
                if (row.go == null) continue;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(row.go.name, EditorStyles.linkLabel))
                        {
                            EditorGUIUtility.PingObject(row.go);
                            Selection.activeGameObject = row.go;
                        }
                        GUILayout.FlexibleSpace();
                        if (row.pending.Count > 0 &&
                            GUILayout.Button(new GUIContent($"専用化 ({row.pending.Count})",
                                    "この追加物の中で使われている共有マテリアルを専用コピーにします"),
                                GUILayout.Width(90)))
                        {
                            foreach (var pair in row.pending)
                                MaterialIsolator.IsolateSingle(_avatar, pair.Key);
                            _cachesDirty = true;
                        }
                    }
                    EditorGUILayout.LabelField(
                        row.sourcePath != null
                            ? row.sourcePath
                            : "(シーンオブジェクト — シーンファイルにのみ存在)",
                        EditorStyles.miniLabel);
                }
            }

            if (_additionsReport.isPrefabInstance)
            {
                EditorGUILayout.Space(4f);
                DrawSectionLabel($"追加されたコンポーネント ({_additionsReport.addedComponents.Count})");
                if (_additionsReport.addedComponents.Count == 0)
                    EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
                foreach (var c in _additionsReport.addedComponents)
                {
                    if (c == null) continue;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"{c.gameObject.name} · {c.GetType().Name}");
                        if (GUILayout.Button("選択", EditorStyles.miniButton, GUILayout.Width(60)))
                        {
                            EditorGUIUtility.PingObject(c.gameObject);
                            Selection.activeGameObject = c.gameObject;
                        }
                    }
                }

                EditorGUILayout.Space(4f);
                DrawSectionLabel($"削除されたコンポーネント ({_additionsReport.removedComponentDescriptions.Count})");
                if (_additionsReport.removedComponentDescriptions.Count == 0)
                    EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
                foreach (var desc in _additionsReport.removedComponentDescriptions)
                {
                    EditorGUILayout.LabelField(desc, EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        // ---------- Not Isolated tab ----------

        private void DrawPendingTab()
        {
            var state = CurrentState;
            var pendingTextures = PendingTextureCount();

            if (_pendingMaterials.Count == 0 && pendingTextures == 0 && _pendingLayers.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    state == null
                        ? "このアバターには専用化できる共有アセットが見つかりませんでした。"
                        : "このアバターのアセットはすべて専用化済みです。✔",
                    MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _search = GUILayout.TextField(_search ?? string.Empty, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));
            }
            var filter = (_search ?? string.Empty).Trim();

            _scrolls[TabPending] = EditorGUILayout.BeginScrollView(_scrolls[TabPending]);

            DrawSectionLabel($"マテリアル ({_pendingMaterials.Count})");
            if (_pendingMaterials.Count == 0)
            {
                EditorGUILayout.LabelField("(すべて専用化済み)", EditorStyles.miniLabel);
            }
            else
            {
                int shown = 0;
                foreach (var pair in _pendingMaterials)
                {
                    if (pair.Key == null) continue;
                    if (filter.Length > 0 &&
                        pair.Key.name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    DrawPendingMaterialRow(pair.Key, pair.Value);
                    shown++;
                }
                if (shown == 0)
                    EditorGUILayout.LabelField("(no match)", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(6f);
            DrawSectionLabel($"テクスチャ ({pendingTextures})");
            if (state == null || state.bindings == null || state.bindings.Count == 0)
            {
                EditorGUILayout.LabelField("(先にマテリアルを専用化してください — テクスチャは専用マテリアル上で管理します)", EditorStyles.miniLabel);
            }
            else if (pendingTextures == 0)
            {
                EditorGUILayout.LabelField("(すべて専用化済み)", EditorStyles.miniLabel);
            }
            else
            {
                int shown = 0;
                foreach (var entry in _textures)
                {
                    if (entry.texture == null || entry.isolated) continue;
                    if (filter.Length > 0 && !MatchesTextureFilter(entry, filter)) continue;
                    DrawTextureListRow(state, entry);
                    shown++;
                }
                if (shown == 0)
                    EditorGUILayout.LabelField("(no match)", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(6f);
            DrawSectionLabel($"アニメーター ({_pendingLayers.Count})");
            if (_pendingLayers.Count == 0)
            {
                EditorGUILayout.LabelField("(共有のままの Playable Layer コントローラーなし)", EditorStyles.miniLabel);
            }
            else if (DrawPendingLayerRows(state, filter) == 0)
            {
                EditorGUILayout.LabelField("(no match)", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawPendingMaterialRow(Material material, int slotCount)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(material, typeof(Material), false);
                }
                GUILayout.Label($"{slotCount} スロット",
                    EditorStyles.miniLabel, GUILayout.Width(60));
                if (GUILayout.Button(new GUIContent("専用化",
                        "このマテリアルだけをコピーし、使っているレンダラーのスロットを差し替えます"),
                    GUILayout.Width(80)))
                {
                    MaterialIsolator.IsolateSingle(_avatar, material);
                    _cachesDirty = true;
                }
            }
        }

        private readonly struct LabelWidthScope : System.IDisposable
        {
            private readonly float _prev;
            public LabelWidthScope(float width)
            {
                _prev = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = width;
            }
            public void Dispose() => EditorGUIUtility.labelWidth = _prev;
        }
    }
}
