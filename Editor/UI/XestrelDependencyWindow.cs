using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Xestrel.Core;
using Xestrel.Detection;
using Xestrel.Isolation;
using Xestrel.Runtime;

namespace Xestrel.UI
{
    /// <summary>
    /// Dependency browser for one or more avatars, as an indented tree (Project-window
    /// style). The first ring shows every asset the hierarchy's components reference,
    /// discovered generically from serialized object references; rows expand on demand
    /// to the asset's own direct dependencies. Isolation state is a color overlay:
    /// orange = still shared and isolatable, green = isolated copy, red = a copy that
    /// another workspace's manifest also tracks, gray = kinds xestrel does not manage.
    /// Rows carry inline Isolate / Restore buttons acting on the primary avatar.
    /// </summary>
    internal sealed class XestrelDependencyWindow : EditorWindow
    {
        private static readonly Color SharedColor = new Color(0.90f, 0.60f, 0.20f);
        private static readonly Color IsolatedColor = new Color(0.35f, 0.75f, 0.40f);
        private static readonly Color ConflictColor = new Color(0.90f, 0.30f, 0.30f);
        private static readonly Color NeutralColor = new Color(0.55f, 0.57f, 0.62f);
        private static readonly Color AvatarColor = new Color(0.55f, 0.65f, 0.90f);
        private static readonly Color HighlightBg = new Color(1f, 0.85f, 0.2f, 0.12f);

        private const int MaxDepth = 8;
        private const int RowBudgetPerRepaint = 2000;

        [SerializeField] private GameObject _avatar;
        [SerializeField] private List<GameObject> _extraAvatars = new List<GameObject>();

        private readonly HashSet<Object> _expanded = new HashSet<Object>();
        private readonly Dictionary<AssetKind, bool> _kindVisible = new Dictionary<AssetKind, bool>();
        private bool _hidePackages = true;
        private string _search = string.Empty;
        private Vector2 _scroll;

        // Caches, rebuilt lazily on Layout when dirty.
        private bool _cachesDirty = true;
        private readonly List<KeyValuePair<GameObject, List<SceneAssetRef>>> _sceneRefs =
            new List<KeyValuePair<GameObject, List<SceneAssetRef>>>();
        private readonly Dictionary<Object, int> _rootRefCounts = new Dictionary<Object, int>();
        private readonly Dictionary<Object, string> _foreignCopyOwners = new Dictionary<Object, string>();
        private readonly Dictionary<Object, List<Object>> _depCache = new Dictionary<Object, List<Object>>();
        private int _rowBudget;
        private bool _truncated;

        [MenuItem("Window/Xestrel/Dependencies")]
        public static void Open()
        {
            var w = GetWindow<XestrelDependencyWindow>();
            w.titleContent = new GUIContent("Xestrel Deps");
            w.minSize = new Vector2(480, 300);
            w.Show();
        }

        public static void OpenFor(GameObject avatar)
        {
            var w = GetWindow<XestrelDependencyWindow>();
            w.titleContent = new GUIContent("Xestrel Deps");
            w.minSize = new Vector2(480, 300);
            if (avatar != null)
            {
                w._avatar = avatar;
                w._cachesDirty = true;
            }
            w.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Xestrel Deps");
            EditorApplication.hierarchyChanged += MarkDirty;
            EditorApplication.projectChanged += MarkDirty;
            Undo.undoRedoPerformed += MarkDirty;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkDirty;
            EditorApplication.projectChanged -= MarkDirty;
            Undo.undoRedoPerformed -= MarkDirty;
        }

        private void MarkDirty()
        {
            _cachesDirty = true;
            Repaint();
        }

        private List<GameObject> Roots()
        {
            var list = new List<GameObject>();
            if (_avatar != null) list.Add(_avatar);
            foreach (var a in _extraAvatars)
            {
                if (a != null && !list.Contains(a)) list.Add(a);
            }
            return list;
        }

        // ---------- caches ----------

        private void RebuildCaches()
        {
            _sceneRefs.Clear();
            _rootRefCounts.Clear();
            _foreignCopyOwners.Clear();
            _depCache.Clear();

            var roots = Roots();
            foreach (var root in roots)
            {
                var refs = AssetDependencyScanner.CollectSceneRefs(root);
                SortRefs(refs);
                _sceneRefs.Add(new KeyValuePair<GameObject, List<SceneAssetRef>>(root, refs));
                foreach (var r in refs)
                {
                    _rootRefCounts.TryGetValue(r.asset, out var n);
                    _rootRefCounts[r.asset] = n + 1;
                }
            }

            // Copies tracked by any workspace manifest other than the displayed
            // avatars' own. Manifests are assets, so this sees duplicated avatars
            // even when their scene is not loaded.
            var own = new HashSet<Object>();
            foreach (var root in roots)
            {
                var state = root.GetComponent<XestrelMaterialIsolation>();
                if (state != null && state.workspaceManifest != null) own.Add(state.workspaceManifest);
            }
            foreach (var manifest in WorkspaceManifests.FindAll())
            {
                if (manifest == null || own.Contains(manifest)) continue;
                var owner = WorkspaceManifests.FolderNameOf(manifest) ?? manifest.avatarName ?? "?";
                if (manifest.bindings != null)
                    foreach (var b in manifest.bindings) { if (b != null) AddForeignCopy(b.copy, owner); }
                if (manifest.textureBindings != null)
                    foreach (var b in manifest.textureBindings) { if (b != null) AddForeignCopy(b.copy, owner); }
                if (manifest.animatorBindings != null)
                    foreach (var b in manifest.animatorBindings) { if (b != null) AddForeignCopy(b.copy, owner); }
                if (manifest.clipBindings != null)
                    foreach (var b in manifest.clipBindings) { if (b != null) AddForeignCopy(b.copy, owner); }
            }
        }

        private void AddForeignCopy(Object copy, string owner)
        {
            if (copy == null) return;
            if (_foreignCopyOwners.TryGetValue(copy, out var existing))
            {
                if (!existing.Contains(owner)) _foreignCopyOwners[copy] = existing + ", " + owner;
            }
            else
            {
                _foreignCopyOwners[copy] = owner;
            }
        }

        private List<Object> Deps(Object asset)
        {
            if (_depCache.TryGetValue(asset, out var cached)) return cached;
            var deps = AssetDependencyScanner.CollectAssetRefs(asset);
            SortAssets(deps);
            _depCache[asset] = deps;
            return deps;
        }

        private static void SortRefs(List<SceneAssetRef> refs)
        {
            refs.Sort((a, b) =>
            {
                int k = AssetDependencyScanner.KindOf(a.asset).CompareTo(AssetDependencyScanner.KindOf(b.asset));
                return k != 0
                    ? k
                    : string.Compare(a.asset.name, b.asset.name, System.StringComparison.OrdinalIgnoreCase);
            });
        }

        private static void SortAssets(List<Object> assets)
        {
            assets.Sort((a, b) =>
            {
                int k = AssetDependencyScanner.KindOf(a).CompareTo(AssetDependencyScanner.KindOf(b));
                return k != 0
                    ? k
                    : string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase);
            });
        }

        private bool IsKindVisible(AssetKind kind) =>
            !_kindVisible.TryGetValue(kind, out var visible) || visible;

        private bool IsVisible(Object asset)
        {
            if (asset == null) return false;
            if (!IsKindVisible(AssetDependencyScanner.KindOf(asset))) return false;
            if (_hidePackages &&
                !AssetDatabase.GetAssetPath(asset).StartsWith("Assets/", System.StringComparison.Ordinal))
                return false;
            return true;
        }

        private int VisibleDepCount(Object asset)
        {
            int count = 0;
            foreach (var dep in Deps(asset))
            {
                if (IsVisible(dep)) count++;
            }
            return count;
        }

        // ---------- OnGUI ----------

        private void OnGUI()
        {
            if (Event.current.type == EventType.Layout && _cachesDirty)
            {
                RebuildCaches();
                _cachesDirty = false;
            }

            DrawToolbar();
            _rowBudget = RowBudgetPerRepaint;
            _truncated = false;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_sceneRefs.Count == 0)
            {
                EditorGUILayout.HelpBox("Set an avatar to browse its asset dependencies.", MessageType.Info);
            }
            foreach (var pair in _sceneRefs)
            {
                DrawAvatarRow(pair.Key, pair.Value);
                EditorGUILayout.Space(4f);
            }
            if (_truncated)
            {
                EditorGUILayout.HelpBox(
                    $"Display truncated at {RowBudgetPerRepaint} rows — collapse branches or use the kind filters.",
                    MessageType.Warning);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var next = (GameObject)EditorGUILayout.ObjectField(
                    _avatar, typeof(GameObject), true, GUILayout.MinWidth(160));
                if (next != _avatar)
                {
                    _avatar = next;
                    _cachesDirty = true;
                }
                if (GUILayout.Button(new GUIContent("Add Selected",
                        "Add the selected avatar as another root to compare sharing between avatars"),
                    EditorStyles.toolbarButton, GUILayout.Width(88)))
                {
                    AddSelectedAvatar();
                }
                if (GUILayout.Button("Clear Extras", EditorStyles.toolbarButton, GUILayout.Width(84)))
                {
                    _extraAvatars.Clear();
                    _cachesDirty = true;
                }
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
                {
                    _cachesDirty = true;
                }
                GUILayout.FlexibleSpace();
                DrawLegend("shared", SharedColor);
                DrawLegend("isolated", IsolatedColor);
                DrawLegend("conflict", ConflictColor);
                DrawLegend("unmanaged", NeutralColor);
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                foreach (AssetKind kind in System.Enum.GetValues(typeof(AssetKind)))
                {
                    var wasVisible = IsKindVisible(kind);
                    var visible = GUILayout.Toggle(wasVisible,
                        AssetDependencyScanner.LabelOf(kind), EditorStyles.toolbarButton);
                    if (visible != wasVisible)
                    {
                        _kindVisible[kind] = visible;
                    }
                }
                var hide = GUILayout.Toggle(_hidePackages, new GUIContent("No Pkg",
                    "Hide assets outside Assets/ (Packages, built-ins)"), EditorStyles.toolbarButton);
                if (hide != _hidePackages)
                {
                    _hidePackages = hide;
                }
                GUILayout.FlexibleSpace();
                _search = GUILayout.TextField(_search ?? string.Empty,
                    EditorStyles.toolbarSearchField, GUILayout.MinWidth(120), GUILayout.MaxWidth(200));
            }
        }

        private static void DrawLegend(string text, Color color)
        {
            var prev = GUI.contentColor;
            GUI.contentColor = color;
            GUILayout.Label("●", GUILayout.Width(12));
            GUI.contentColor = prev;
            GUILayout.Label(text, EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
        }

        private void AddSelectedAvatar()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;
            var root = AvatarRootRecogniser.ResolveAvatarRoot(go) ?? go;
            if (root == _avatar || _extraAvatars.Contains(root)) return;
            if (_avatar == null) _avatar = root;
            else _extraAvatars.Add(root);
            _cachesDirty = true;
        }

        // ---------- tree ----------

        private void DrawAvatarRow(GameObject root, List<SceneAssetRef> refs)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var prev = GUI.contentColor;
                GUI.contentColor = AvatarColor;
                GUILayout.Label("●", GUILayout.Width(12));
                GUI.contentColor = prev;
                if (GUILayout.Button(root.name, EditorStyles.boldLabel, GUILayout.ExpandWidth(false)))
                {
                    EditorGUIUtility.PingObject(root);
                    Selection.activeGameObject = root;
                }
                GUILayout.Label($"{refs.Count} referenced asset(s)", EditorStyles.miniLabel);
            }

            var chain = new HashSet<Object>();
            foreach (var sceneRef in refs)
            {
                if (!IsVisible(sceneRef.asset)) continue;
                DrawAssetRow(sceneRef.asset, 1, string.Join("\n", sceneRef.usedBy), chain);
            }
        }

        private void DrawAssetRow(Object asset, int indent, string usedByTooltip, HashSet<Object> chain)
        {
            if (_rowBudget <= 0)
            {
                _truncated = true;
                return;
            }
            _rowBudget--;

            var rowRect = EditorGUILayout.GetControlRect(false, 18f);
            if (_search.Length > 0 &&
                asset.name.IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                EditorGUI.DrawRect(rowRect, HighlightBg);
            }

            float x = rowRect.x + indent * 16f;
            var expanded = _expanded.Contains(asset);
            var cyclic = chain.Contains(asset);
            int depCount = cyclic ? 0 : VisibleDepCount(asset);

            var expanderRect = new Rect(x, rowRect.y + 1f, 38f, rowRect.height - 2f);
            if (depCount > 0)
            {
                if (GUI.Button(expanderRect, (expanded ? "▾ " : "▸ ") + depCount, EditorStyles.miniButton))
                {
                    if (!_expanded.Add(asset)) _expanded.Remove(asset);
                    Repaint();
                    GUIUtility.ExitGUI();
                }
            }
            x += 42f;

            var dotRect = new Rect(x, rowRect.y, 12f, rowRect.height);
            var prevColor = GUI.contentColor;
            GUI.contentColor = ColorFor(asset);
            GUI.Label(dotRect, "●");
            GUI.contentColor = prevColor;
            x += 14f;

            // Inline action (right-aligned), then badges to its left, name fills the rest.
            const float ActionWidth = 64f;
            var actionRect = new Rect(rowRect.xMax - ActionWidth, rowRect.y + 1f, ActionWidth, rowRect.height - 2f);
            DrawAction(actionRect, asset);

            _foreignCopyOwners.TryGetValue(asset, out var foreignOwner);
            _rootRefCounts.TryGetValue(asset, out var rootCount);
            var badge = AssetDependencyScanner.LabelOf(AssetDependencyScanner.KindOf(asset));
            if (rootCount > 1) badge += $" · ×{rootCount} avatars";
            if (cyclic) badge += " · cyclic";
            if (foreignOwner != null) badge += $" · ⚠ {foreignOwner}";
            var badgeSize = EditorStyles.miniLabel.CalcSize(new GUIContent(badge));
            var badgeRect = new Rect(actionRect.x - 4f - badgeSize.x, rowRect.y, badgeSize.x, rowRect.height);
            var prevBadgeColor = GUI.contentColor;
            if (foreignOwner != null) GUI.contentColor = ConflictColor;
            GUI.Label(badgeRect, badge, EditorStyles.miniLabel);
            GUI.contentColor = prevBadgeColor;

            var path = AssetDatabase.GetAssetPath(asset);
            var tooltip = string.IsNullOrEmpty(usedByTooltip) ? path : path + "\nUsed by:\n" + usedByTooltip;
            var nameRect = new Rect(x, rowRect.y, badgeRect.x - 6f - x, rowRect.height);
            if (GUI.Button(nameRect, new GUIContent(asset.name, tooltip), EditorStyles.linkLabel))
            {
                EditorGUIUtility.PingObject(asset);
                Selection.activeObject = asset;
            }

            if (expanded && depCount > 0 && indent < MaxDepth)
            {
                chain.Add(asset);
                foreach (var dep in Deps(asset))
                {
                    if (!IsVisible(dep)) continue;
                    DrawAssetRow(dep, indent + 1, null, chain);
                }
                chain.Remove(asset);
            }
        }

        // One contextual action per row: Isolate for still-shared manageable assets,
        // Restore for this workspace's copies. Acts on the primary avatar.
        private void DrawAction(Rect rect, Object asset)
        {
            var avatar = _avatar;
            if (avatar == null) return;
            var state = avatar.GetComponent<XestrelMaterialIsolation>();
            var isolated = IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(asset));

            if (asset is Material mat)
            {
                if (!isolated)
                {
                    if (GUI.Button(rect, new GUIContent("Isolate",
                            $"Copy this material for \"{avatar.name}\" and rewire its renderer slots"),
                        EditorStyles.miniButton))
                    {
                        MaterialIsolator.IsolateSingle(avatar, mat);
                        MarkDirty();
                        GUIUtility.ExitGUI();
                    }
                }
                else if (state != null)
                {
                    var binding = FindMaterialBinding(state, mat);
                    if (binding != null && GUI.Button(rect, new GUIContent("Restore",
                            "Point renderers back at the original (copy stays on disk)"),
                        EditorStyles.miniButton))
                    {
                        MaterialIsolator.RestoreBinding(state, binding);
                        MarkDirty();
                        GUIUtility.ExitGUI();
                    }
                }
            }
            else if (asset is Texture tex)
            {
                if (!isolated && state != null)
                {
                    if (GUI.Button(rect, new GUIContent("Isolate",
                            $"Copy this texture for \"{avatar.name}\" and rewire every material slot that uses it"),
                        EditorStyles.miniButton))
                    {
                        TextureIsolator.IsolateTextureAcrossMaterials(state, tex);
                        MarkDirty();
                        GUIUtility.ExitGUI();
                    }
                }
                else if (isolated && state != null && TextureIsolator.FindOriginal(state, tex) != null)
                {
                    if (GUI.Button(rect, new GUIContent("Restore",
                            "Point every slot that uses this copy back at the shared original"),
                        EditorStyles.miniButton))
                    {
                        TextureIsolator.RestoreTextureAcrossMaterials(state, tex);
                        MarkDirty();
                        GUIUtility.ExitGUI();
                    }
                }
            }
            else if (asset is RuntimeAnimatorController controller)
            {
                if (!isolated && state != null)
                {
                    if (GUI.Button(rect, new GUIContent("Isolate",
                            $"Copy this controller (and its clips) for \"{avatar.name}\""),
                        EditorStyles.miniButton))
                    {
                        AnimatorIsolator.IsolateController(state, controller);
                        MarkDirty();
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        private static XestrelMaterialBinding FindMaterialBinding(XestrelMaterialIsolation state, Material copy)
        {
            if (state.bindings == null) return null;
            foreach (var b in state.bindings)
            {
                if (b != null && b.copy == copy) return b;
            }
            return null;
        }

        private Color ColorFor(Object asset)
        {
            var kind = AssetDependencyScanner.KindOf(asset);
            var manageable =
                kind == AssetKind.Material || kind == AssetKind.Texture ||
                kind == AssetKind.Animator || kind == AssetKind.Clip;
            if (!manageable) return NeutralColor;

            if (!IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(asset))) return SharedColor;
            return _foreignCopyOwners.ContainsKey(asset) ? ConflictColor : IsolatedColor;
        }
    }
}
