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
        private const int TabPending = 3;

        private GameObject _avatar;
        private bool _lockAvatar;
        private int _tab;
        private readonly Vector2[] _scrolls = new Vector2[4];
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
            _pendingMaterials = MaterialIsolator.CollectPendingMaterials(_avatar);
            RebuildTextureEntries();
            RebuildPendingLayers();
            DetectSharedCopies();
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
                new GUIContent("Materials"),
                new GUIContent("Textures"),
                new GUIContent("Animators"),
                new GUIContent(pendingTotal > 0 ? $"Not Isolated ({pendingTotal})" : "Not Isolated"),
            };
            _tab = GUILayout.Toolbar(_tab, tabs, GUILayout.Height(22f));
            EditorGUILayout.Space(2f);

            switch (_tab)
            {
                case TabMaterials: DrawMaterialsTab(); break;
                case TabTextures: DrawTexturesTab(); break;
                case TabAnimators: DrawAnimatorsTab(); break;
                case TabPending: DrawPendingTab(); break;
            }
        }

        // ---------- header ----------

        private void DrawTitleBar()
        {
            var rect = EditorGUILayout.GetControlRect(false, 28f);
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.18f));
            var labelRect = new Rect(rect.x + 10f, rect.y + 6f, rect.width - 20f, rect.height - 8f);
            GUI.Label(labelRect, "Asset Isolation", EditorStyles.boldLabel);
        }

        private void DrawAvatarSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new LabelWidthScope(60f))
                    {
                        var next = (GameObject)EditorGUILayout.ObjectField("Avatar", _avatar, typeof(GameObject), true);
                        if (next != _avatar)
                        {
                            _avatar = next;
                            _cachesDirty = true;
                        }
                    }
                    _lockAvatar = GUILayout.Toggle(
                        _lockAvatar,
                        new GUIContent("", "Lock: keep this avatar even when the Hierarchy selection changes"),
                        "IN LockButton",
                        GUILayout.Width(16f), GUILayout.Height(16f));
                }

                EditorGUILayout.Space(4f);

                using (new EditorGUILayout.HorizontalScope())
                using (new EditorGUI.DisabledScope(_avatar == null))
                {
                    var isolateLabel = _pendingMaterials.Count > 0 ? $"Isolate ({_pendingMaterials.Count})" : "Isolate";
                    if (GUILayout.Button(new GUIContent(isolateLabel,
                            "Copy every shared material on this avatar's renderers into Assets/Xestrel/<AvatarName>/Materials/"),
                        GUILayout.Height(24)))
                    {
                        Isolator.Isolate(_avatar);
                        _cachesDirty = true;
                    }
                    using (new EditorGUI.DisabledScope(CurrentState == null))
                    {
                        if (GUILayout.Button("Restore", GUILayout.Height(24)) && ConfirmRestoreAll())
                        {
                            Isolator.Restore(CurrentState);
                            _cachesDirty = true;
                        }
                        if (GUILayout.Button(new GUIContent("Folder",
                                "Ping this avatar's copy folder in the Project window"),
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

        private bool ConfirmRestoreAll()
        {
            var state = CurrentState;
            var name = state != null ? state.avatarName : "avatar";
            return EditorUtility.DisplayDialog(
                "Xestrel — Restore",
                $"Point \"{name}\" back at the original shared assets?\n\n" +
                "All material / texture / animator bindings will be cleared. " +
                "Copy assets under Assets/Xestrel/ stay on disk.",
                "Restore", "Cancel");
        }

        private void PingAvatarFolder()
        {
            var state = CurrentState;
            if (state == null) return;
            var dir = IsolationPaths.AvatarDir(state.avatarName);
            var folder = AssetDatabase.LoadAssetAtPath<Object>(dir);
            if (folder == null)
            {
                EditorUtility.DisplayDialog("Xestrel", $"No copy folder yet:\n{dir}", "OK");
                return;
            }
            EditorGUIUtility.PingObject(folder);
            Selection.activeObject = folder;
        }

        private void DrawStatus()
        {
            if (_avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar in the scene.", MessageType.Info);
                return;
            }
            var state = CurrentState;
            if (state == null)
            {
                var msg = _pendingMaterials.Count > 0
                    ? $"Not isolated yet. Press Isolate to copy {_pendingMaterials.Count} shared material(s)."
                    : "Not isolated yet. Press Isolate to copy this avatar's materials.";
                EditorGUILayout.HelpBox(msg, MessageType.Info);
                return;
            }

            if (_sharesCopiesWith != null)
            {
                var otherName = _sharesCopiesWith.gameObject != null ? _sharesCopiesWith.gameObject.name : "?";
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox(
                        $"This avatar shares copy assets with \"{otherName}\" — a duplicated isolated avatar? " +
                        "Edits to those copies affect both. Press Fork to give this avatar its own " +
                        "independent copies (current edits are inherited; the other avatar is untouched).",
                        MessageType.Warning);
                    if (GUILayout.Button(new GUIContent("Fork",
                            "Re-copy every bound material / texture / animator into a new workspace folder and rewire this avatar to the forks"),
                        GUILayout.Width(60), GUILayout.Height(52)))
                    {
                        if (EditorUtility.DisplayDialog(
                                "Xestrel — Fork Copies",
                                $"Give \"{_avatar.name}\" its own independent copies under a new folder in Assets/Xestrel/?\n\n" +
                                $"All edits made so far are inherited. \"{otherName}\" keeps the current copies and is not modified.",
                                "Fork", "Cancel"))
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
                        "Some bindings reference deleted assets.", MessageType.Warning);
                    if (GUILayout.Button("Prune", GUILayout.Width(60), GUILayout.Height(38)))
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
                EditorGUILayout.HelpBox("No bindings recorded. Press Isolate.", MessageType.Info);
                return;
            }
            var pendingNote = _pendingMaterials.Count > 0
                ? $" — {_pendingMaterials.Count} material(s) not isolated yet"
                : "";
            var renamedNote = _avatar.name != state.avatarName
                ? $"\nGameObject is now \"{_avatar.name}\" — copies keep using Assets/Xestrel/{state.avatarName}/."
                : "";
            EditorGUILayout.HelpBox(
                $"\"{state.avatarName}\": {matCount} mat / {texCount} tex / {animCount} anim / {clipCount} clip copy(ies){pendingNote}.{renamedNote}",
                _pendingMaterials.Count > 0 ? MessageType.Warning : MessageType.None);
        }

        // ---------- Materials tab ----------

        private void DrawMaterialsTab()
        {
            var state = CurrentState;
            if (state == null || state.bindings == null || state.bindings.Count == 0)
            {
                EditorGUILayout.HelpBox("No isolated materials yet. Press Isolate.", MessageType.Info);
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
                EditorGUILayout.HelpBox("No bindings match the filter.", MessageType.None);
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
                    if (GUILayout.Button("Expand", EditorStyles.toolbarButton, GUILayout.Width(58)))
                    {
                        foreach (var b in state.bindings)
                            if (b != null && b.copy != null) _expanded.Add(b.copy);
                    }
                    if (GUILayout.Button("Collapse", EditorStyles.toolbarButton, GUILayout.Width(64)))
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
                DrawSectionLabel("Textures");
                DrawTextureProperties(b.copy);
            }
        }

        private void DrawBindingActions(XestrelMaterialIsolation state, XestrelMaterialBinding b)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Isolate All Textures",
                        "Copy every shared texture referenced by this material"),
                    EditorStyles.miniButton, GUILayout.Width(130)))
                {
                    TextureIsolator.IsolateAllProperties(state, b.copy);
                    _cachesDirty = true;
                }
                if (GUILayout.Button(new GUIContent("Restore Material",
                        "Point renderers back at the original material and drop this binding"),
                    EditorStyles.miniButton, GUILayout.Width(110)))
                {
                    var name = b.original != null ? b.original.name : b.copy.name;
                    if (EditorUtility.DisplayDialog(
                            "Xestrel — Restore Material",
                            $"Point renderers back at the original \"{name}\"?\n\n" +
                            "The copy asset stays on disk.",
                            "Restore", "Cancel"))
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

            if (GUI.Button(pingRect, "Select", EditorStyles.miniButton))
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
                    EditorGUILayout.ObjectField("Original", b.original, typeof(Material), false);
                }
                EditorGUILayout.ObjectField("Copy (editable)", b.copy, typeof(Material), false);
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
                EditorGUILayout.HelpBox("Material has no shader.", MessageType.Warning);
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
                EditorGUILayout.LabelField("(no visible texture properties)", EditorStyles.miniLabel);
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
                isolated ? propName + " (isolated copy)" : propName);
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
                if (GUI.Button(buttonRect, new GUIContent("Restore",
                        "Point this property back at the shared original (copy stays on disk)"),
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
                    var btnLabel = current == null ? "—" : (isolated ? "Isolated" : "Isolate");
                    if (GUI.Button(buttonRect, btnLabel, EditorStyles.miniButton))
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
                    "No isolated materials yet. Textures are listed from the copy materials, so press Isolate first.",
                    MessageType.Info);
                return;
            }
            if (_textures.Count == 0)
            {
                EditorGUILayout.HelpBox("The copy materials reference no textures.", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _search = GUILayout.TextField(_search ?? string.Empty, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));
                var isolatedCount = _textures.Count - PendingTextureCount();
                GUILayout.Label($"{isolatedCount}/{_textures.Count} isolated", EditorStyles.miniLabel, GUILayout.Width(90));
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
                EditorGUILayout.HelpBox("No textures match the filter.", MessageType.None);
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
                    entry.isolated ? "Isolated per-avatar copy" : "Shared texture"), EditorStyles.boldLabel);

                var usesSummary = entry.uses.Count == 1
                    ? entry.uses[0]
                    : $"{entry.uses.Count} slots: " + string.Join(", ", entry.uses);
                GUI.Label(usesRect, new GUIContent(Truncate(usesSummary, 120), string.Join("\n", entry.uses)),
                    EditorStyles.miniLabel);

                var buttonY = rowRect.y + (TextureFieldHeight - EditorGUIUtility.singleLineHeight) * 0.5f;
                var selectRect = new Rect(rowRect.xMax - buttonsWidth + Gap, buttonY, SelectWidth, EditorGUIUtility.singleLineHeight);
                var actionRect = new Rect(rowRect.xMax - ButtonWidth, buttonY, ButtonWidth, EditorGUIUtility.singleLineHeight);

                if (GUI.Button(selectRect, "Select", EditorStyles.miniButton))
                {
                    EditorGUIUtility.PingObject(entry.texture);
                    Selection.activeObject = entry.texture;
                }

                if (!entry.isolated)
                {
                    if (GUI.Button(actionRect, new GUIContent("Isolate",
                            "Copy this texture and rewire every material slot that uses it"),
                        EditorStyles.miniButton))
                    {
                        TextureIsolator.IsolateTextureAcrossMaterials(state, entry.texture);
                        _cachesDirty = true;
                    }
                }
                else if (entry.original != null)
                {
                    if (GUI.Button(actionRect, new GUIContent("Restore",
                            "Point every slot that uses this copy back at the shared original"),
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
                        GUI.Button(actionRect, "Isolated", EditorStyles.miniButton);
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
                DrawSectionLabel("Playable layers (shared)");
                DrawPendingLayerRows(state, string.Empty);
                EditorGUILayout.Space(4f);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _pendingAnimator = (RuntimeAnimatorController)EditorGUILayout.ObjectField(
                    _pendingAnimator, typeof(RuntimeAnimatorController), false);

                using (new EditorGUI.DisabledScope(_pendingAnimator == null || state == null))
                {
                    if (GUILayout.Button("Isolate", GUILayout.Width(80)))
                    {
                        AnimatorIsolator.IsolateController(state, _pendingAnimator);
                        _pendingAnimator = null;
                        _cachesDirty = true;
                    }
                }
            }

            if (state == null)
            {
                EditorGUILayout.HelpBox("Run material Isolate first to create the state component.", MessageType.None);
                return;
            }

            if (state.animatorBindings == null || state.animatorBindings.Count == 0)
            {
                EditorGUILayout.LabelField("(no isolated animators)", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.Space(2f);
            DrawSectionLabel("Isolated controllers");
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
                        if (GUILayout.Button(new GUIContent("Isolate",
                                "Copy this playable-layer controller (and its clips) and rewire the descriptor"),
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
                var label = b.original != null ? b.original.name : "<missing>";
                EditorGUILayout.LabelField(label, GUILayout.MinWidth(80));
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(b.copy, typeof(RuntimeAnimatorController), false);
                }
                if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    EditorGUIUtility.PingObject(b.copy);
                    Selection.activeObject = b.copy;
                }
            }
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
                        ? "Nothing to isolate was found on this avatar's renderers."
                        : "Everything on this avatar is isolated. ✔",
                    MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _search = GUILayout.TextField(_search ?? string.Empty, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));
            }
            var filter = (_search ?? string.Empty).Trim();

            _scrolls[TabPending] = EditorGUILayout.BeginScrollView(_scrolls[TabPending]);

            DrawSectionLabel($"Materials ({_pendingMaterials.Count})");
            if (_pendingMaterials.Count == 0)
            {
                EditorGUILayout.LabelField("(all materials isolated)", EditorStyles.miniLabel);
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
            DrawSectionLabel($"Textures ({pendingTextures})");
            if (state == null || state.bindings == null || state.bindings.Count == 0)
            {
                EditorGUILayout.LabelField("(isolate materials first — textures are tracked on the copies)", EditorStyles.miniLabel);
            }
            else if (pendingTextures == 0)
            {
                EditorGUILayout.LabelField("(all referenced textures isolated)", EditorStyles.miniLabel);
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
            DrawSectionLabel($"Animators ({_pendingLayers.Count})");
            if (_pendingLayers.Count == 0)
            {
                EditorGUILayout.LabelField("(no shared playable-layer controllers)", EditorStyles.miniLabel);
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
                GUILayout.Label(slotCount == 1 ? "1 slot" : $"{slotCount} slots",
                    EditorStyles.miniLabel, GUILayout.Width(50));
                if (GUILayout.Button(new GUIContent("Isolate",
                        "Copy just this material and rewire the renderer slots that use it"),
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
