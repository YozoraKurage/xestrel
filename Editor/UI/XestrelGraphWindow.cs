using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Xestrel.Core;
using Xestrel.Detection;
using Xestrel.Isolation;
using Xestrel.Runtime;

namespace Xestrel.UI
{
    /// <summary>
    /// Dependency graph of one or more avatars: the first ring shows every asset the
    /// hierarchy's components reference; nodes expand on demand to their own direct
    /// dependencies. Isolation state is an overlay — orange for still-shared assets
    /// xestrel could isolate, green for isolated copies, red for copies another
    /// workspace's manifest also tracks, gray for kinds xestrel does not manage.
    /// Right-click a node for Isolate / Restore, double-click to ping.
    /// </summary>
    internal sealed class XestrelGraphWindow : EditorWindow
    {
        [SerializeField] private GameObject _avatar;
        [SerializeField] private List<GameObject> _extraAvatars = new List<GameObject>();

        private readonly HashSet<Object> _expandedAssets = new HashSet<Object>();
        private readonly Dictionary<AssetKind, bool> _kindVisible = new Dictionary<AssetKind, bool>();
        private bool _hidePackages = true;
        private string _search = string.Empty;

        private XestrelDependencyGraphView _graph;
        private ObjectField _avatarField;

        internal GameObject PrimaryAvatar => _avatar;

        internal List<GameObject> Roots
        {
            get
            {
                var list = new List<GameObject>();
                if (_avatar != null) list.Add(_avatar);
                foreach (var a in _extraAvatars)
                {
                    if (a != null && !list.Contains(a)) list.Add(a);
                }
                return list;
            }
        }

        [MenuItem("Window/Xestrel/Dependency Graph")]
        public static void Open()
        {
            var w = GetWindow<XestrelGraphWindow>();
            w.titleContent = new GUIContent("Xestrel Graph");
            w.Show();
        }

        public static void OpenFor(GameObject avatar)
        {
            var w = GetWindow<XestrelGraphWindow>();
            w.titleContent = new GUIContent("Xestrel Graph");
            if (avatar != null)
            {
                w._avatar = avatar;
                if (w._avatarField != null) w._avatarField.SetValueWithoutNotify(avatar);
                w.RebuildGraph();
            }
            w.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("Xestrel Graph");
            rootVisualElement.Clear();

            var row1 = new Toolbar();
            _avatarField = new ObjectField { objectType = typeof(GameObject), allowSceneObjects = true };
            _avatarField.style.minWidth = 200;
            _avatarField.SetValueWithoutNotify(_avatar);
            _avatarField.RegisterValueChangedCallback(evt =>
            {
                _avatar = evt.newValue as GameObject;
                RebuildGraph();
            });
            row1.Add(_avatarField);
            row1.Add(new ToolbarButton(AddSelectedAvatar)
            {
                text = "Add Selected",
                tooltip = "Add the selected avatar as another root to compare sharing between avatars",
            });
            row1.Add(new ToolbarButton(() => { _extraAvatars.Clear(); RebuildGraph(); }) { text = "Clear Extras" });
            row1.Add(new ToolbarButton(RebuildGraph) { text = "Refresh" });
            row1.Add(MakeLegend("● shared", XestrelDependencyGraphView.SharedColor));
            row1.Add(MakeLegend("● isolated", XestrelDependencyGraphView.IsolatedColor));
            row1.Add(MakeLegend("● conflict", XestrelDependencyGraphView.ConflictColor));
            row1.Add(MakeLegend("● unmanaged", XestrelDependencyGraphView.NeutralColor));
            rootVisualElement.Add(row1);

            var row2 = new Toolbar();
            foreach (AssetKind kind in System.Enum.GetValues(typeof(AssetKind)))
            {
                var k = kind;
                var toggle = new ToolbarToggle { text = AssetDependencyScanner.LabelOf(k), value = IsKindVisible(k) };
                toggle.RegisterValueChangedCallback(evt =>
                {
                    _kindVisible[k] = evt.newValue;
                    RebuildGraph();
                });
                row2.Add(toggle);
            }
            var packagesToggle = new ToolbarToggle { text = "Hide Packages", value = _hidePackages };
            packagesToggle.RegisterValueChangedCallback(evt =>
            {
                _hidePackages = evt.newValue;
                RebuildGraph();
            });
            row2.Add(packagesToggle);
            var search = new ToolbarSearchField();
            search.style.width = 160;
            search.RegisterValueChangedCallback(evt =>
            {
                _search = evt.newValue ?? string.Empty;
                RebuildGraph();
            });
            row2.Add(search);
            rootVisualElement.Add(row2);

            _graph = new XestrelDependencyGraphView(this);
            _graph.style.flexGrow = 1;
            rootVisualElement.Add(_graph);
            RebuildGraph();
        }

        private void AddSelectedAvatar()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;
            var root = AvatarRootRecogniser.ResolveAvatarRoot(go) ?? go;
            if (root == _avatar || _extraAvatars.Contains(root)) return;
            if (_avatar == null)
            {
                _avatar = root;
                _avatarField.SetValueWithoutNotify(root);
            }
            else
            {
                _extraAvatars.Add(root);
            }
            RebuildGraph();
        }

        private static Label MakeLegend(string text, Color color)
        {
            var label = new Label(text);
            label.style.color = color;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.marginLeft = 8;
            return label;
        }

        internal void RebuildGraph()
        {
            if (_graph != null) _graph.Rebuild();
        }

        internal bool IsExpanded(Object asset) => _expandedAssets.Contains(asset);

        internal void ToggleExpand(Object asset)
        {
            if (!_expandedAssets.Add(asset)) _expandedAssets.Remove(asset);
            RebuildGraph();
        }

        internal bool IsKindVisible(AssetKind kind) =>
            !_kindVisible.TryGetValue(kind, out var visible) || visible;

        internal bool IsVisible(Object asset)
        {
            if (asset == null) return false;
            if (!IsKindVisible(AssetDependencyScanner.KindOf(asset))) return false;
            if (_hidePackages)
            {
                var path = AssetDatabase.GetAssetPath(asset);
                if (!path.StartsWith("Assets/", System.StringComparison.Ordinal)) return false;
            }
            return true;
        }

        internal bool MatchesSearch(string name) =>
            _search.Length > 0 &&
            name.IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) >= 0;

        // Isolate / Restore act on the primary avatar's workspace (the object field),
        // which the menu labels make explicit.
        internal void AppendIsolationActions(DropdownMenu menu, Object asset)
        {
            var avatar = PrimaryAvatar;
            if (avatar == null) return;
            var state = avatar.GetComponent<XestrelMaterialIsolation>();
            var isolated = IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(asset));

            if (asset is Material mat)
            {
                if (!isolated)
                {
                    menu.AppendAction($"Isolate for \"{avatar.name}\"", _ =>
                    {
                        MaterialIsolator.IsolateSingle(avatar, mat);
                        RebuildGraph();
                    });
                }
                else if (state != null)
                {
                    var binding = FindMaterialBinding(state, mat);
                    menu.AppendAction("Restore original", _ =>
                    {
                        MaterialIsolator.RestoreBinding(state, binding);
                        RebuildGraph();
                    }, binding != null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                }
            }
            else if (asset is Texture tex)
            {
                if (!isolated && state != null)
                {
                    menu.AppendAction($"Isolate for \"{avatar.name}\"", _ =>
                    {
                        TextureIsolator.IsolateTextureAcrossMaterials(state, tex);
                        RebuildGraph();
                    });
                }
                else if (isolated && state != null && TextureIsolator.FindOriginal(state, tex) != null)
                {
                    menu.AppendAction("Restore original", _ =>
                    {
                        TextureIsolator.RestoreTextureAcrossMaterials(state, tex);
                        RebuildGraph();
                    });
                }
            }
            else if (asset is RuntimeAnimatorController controller)
            {
                if (!isolated && state != null)
                {
                    menu.AppendAction($"Isolate for \"{avatar.name}\"", _ =>
                    {
                        AnimatorIsolator.IsolateController(state, controller);
                        RebuildGraph();
                    });
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
    }

    internal sealed class DependencyNode : Node
    {
        public readonly Object asset; // null for avatar root nodes
        private readonly XestrelGraphWindow _window;

        public DependencyNode(XestrelGraphWindow window, Object nodeAsset)
        {
            _window = window;
            asset = nodeAsset;
            capabilities &= ~(Capabilities.Deletable | Capabilities.Renamable | Capabilities.Copiable);
            RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2 && asset != null)
                {
                    EditorGUIUtility.PingObject(asset);
                    Selection.activeObject = asset;
                }
            });
        }

        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            if (asset == null) return;
            evt.menu.AppendAction("Ping", _ =>
            {
                EditorGUIUtility.PingObject(asset);
                Selection.activeObject = asset;
            });
            _window.AppendIsolationActions(evt.menu, asset);
        }
    }

    internal sealed class XestrelDependencyGraphView : GraphView
    {
        public static readonly Color SharedColor = new Color(0.85f, 0.55f, 0.15f);
        public static readonly Color IsolatedColor = new Color(0.30f, 0.65f, 0.35f);
        public static readonly Color ConflictColor = new Color(0.85f, 0.25f, 0.25f);
        public static readonly Color NeutralColor = new Color(0.32f, 0.34f, 0.40f);
        private static readonly Color AvatarColor = new Color(0.35f, 0.40f, 0.55f);
        private static readonly Color HighlightColor = new Color(1f, 0.85f, 0.2f);

        private const float ColumnWidth = 300f;
        private const float RowHeight = 90f;
        private const float NodeWidth = 220f;
        private const int NodeCap = 400;
        private const int MaxDepth = 8;

        private readonly XestrelGraphWindow _window;
        private readonly Dictionary<Object, DependencyNode> _nodes = new Dictionary<Object, DependencyNode>();
        private readonly Dictionary<Node, HashSet<Node>> _edges = new Dictionary<Node, HashSet<Node>>();
        private readonly List<float> _columnY = new List<float>();
        private HashSet<Object> _foreignCopies = new HashSet<Object>();
        private int _nodeBudget;
        private bool _capWarned;

        public XestrelDependencyGraphView(XestrelGraphWindow window)
        {
            _window = window;
            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
        }

        public void Rebuild()
        {
            var stale = new List<GraphElement>();
            graphElements.ForEach(e => stale.Add(e));
            DeleteElements(stale);
            _nodes.Clear();
            _edges.Clear();
            _columnY.Clear();
            _nodeBudget = NodeCap;
            _capWarned = false;

            var roots = _window.Roots;
            if (roots.Count == 0) return;
            _foreignCopies = CollectForeignCopies(roots);

            foreach (var root in roots)
            {
                var rootNode = new DependencyNode(_window, null) { title = root.name };
                rootNode.titleContainer.style.backgroundColor = AvatarColor;
                rootNode.tooltip = "Avatar root";
                AddPorts(rootNode);
                rootNode.SetPosition(new Rect(40f, 40f + NextY(0), NodeWidth, 80f));
                AddElement(rootNode);

                foreach (var sceneRef in AssetDependencyScanner.CollectSceneRefs(root))
                {
                    if (!_window.IsVisible(sceneRef.asset)) continue;
                    var node = EnsureNode(sceneRef.asset, 1, string.Join("\n", sceneRef.usedBy));
                    if (node == null) continue;
                    Connect(rootNode, node);
                    if (_window.IsExpanded(sceneRef.asset))
                        ExpandInto(node, sceneRef.asset, 1, new HashSet<Object> { sceneRef.asset });
                }
            }
        }

        private void ExpandInto(DependencyNode parentNode, Object parentAsset, int depth, HashSet<Object> stack)
        {
            if (depth >= MaxDepth) return;
            foreach (var dep in AssetDependencyScanner.CollectAssetRefs(parentAsset))
            {
                if (!_window.IsVisible(dep)) continue;
                var node = EnsureNode(dep, depth + 1, null);
                if (node == null) continue;
                Connect(parentNode, node);
                if (_window.IsExpanded(dep) && stack.Add(dep))
                {
                    ExpandInto(node, dep, depth + 1, stack);
                    stack.Remove(dep);
                }
            }
        }

        private DependencyNode EnsureNode(Object asset, int column, string usedBy)
        {
            if (_nodes.TryGetValue(asset, out var existing)) return existing;
            if (_nodeBudget <= 0)
            {
                if (!_capWarned)
                {
                    _capWarned = true;
                    XestrelLog.Warn(XestrelLogCategory.UI,
                        $"Dependency graph capped at {NodeCap} nodes; collapse or filter to see the rest");
                }
                return null;
            }
            _nodeBudget--;

            var node = new DependencyNode(_window, asset) { title = asset.name };
            node.titleContainer.style.backgroundColor = ColorFor(asset);
            var path = AssetDatabase.GetAssetPath(asset);
            node.tooltip = string.IsNullOrEmpty(usedBy) ? path : path + "\nUsed by:\n" + usedBy;
            AddPorts(node);

            int depCount = CountVisibleDeps(asset);
            if (depCount > 0)
            {
                var expanded = _window.IsExpanded(asset);
                var button = new Button(() => _window.ToggleExpand(asset))
                {
                    text = expanded ? "−" : $"+{depCount}",
                    tooltip = expanded ? "Collapse dependencies" : "Expand direct dependencies",
                };
                node.titleButtonContainer.Add(button);
            }

            if (_window.MatchesSearch(asset.name))
            {
                node.style.borderTopWidth = 2;
                node.style.borderBottomWidth = 2;
                node.style.borderLeftWidth = 2;
                node.style.borderRightWidth = 2;
                node.style.borderTopColor = HighlightColor;
                node.style.borderBottomColor = HighlightColor;
                node.style.borderLeftColor = HighlightColor;
                node.style.borderRightColor = HighlightColor;
            }

            node.SetPosition(new Rect(40f + column * ColumnWidth, 40f + NextY(column), NodeWidth, 80f));
            AddElement(node);
            _nodes[asset] = node;
            return node;
        }

        private int CountVisibleDeps(Object asset)
        {
            int count = 0;
            foreach (var dep in AssetDependencyScanner.CollectAssetRefs(asset))
            {
                if (_window.IsVisible(dep)) count++;
            }
            return count;
        }

        private float NextY(int column)
        {
            while (_columnY.Count <= column) _columnY.Add(0f);
            var y = _columnY[column];
            _columnY[column] += RowHeight;
            return y;
        }

        // Copies tracked by any workspace manifest other than the displayed avatars'
        // own. Manifests are assets, so this sees duplicated avatars even when their
        // scene is not loaded.
        private static HashSet<Object> CollectForeignCopies(List<GameObject> roots)
        {
            var own = new HashSet<Object>();
            foreach (var root in roots)
            {
                var state = root.GetComponent<XestrelMaterialIsolation>();
                if (state != null && state.workspaceManifest != null) own.Add(state.workspaceManifest);
            }

            var result = new HashSet<Object>();
            foreach (var manifest in WorkspaceManifests.FindAll())
            {
                if (manifest == null || own.Contains(manifest)) continue;
                if (manifest.bindings != null)
                    foreach (var b in manifest.bindings) { if (b != null && b.copy != null) result.Add(b.copy); }
                if (manifest.textureBindings != null)
                    foreach (var b in manifest.textureBindings) { if (b != null && b.copy != null) result.Add(b.copy); }
                if (manifest.animatorBindings != null)
                    foreach (var b in manifest.animatorBindings) { if (b != null && b.copy != null) result.Add(b.copy); }
                if (manifest.clipBindings != null)
                    foreach (var b in manifest.clipBindings) { if (b != null && b.copy != null) result.Add(b.copy); }
            }
            return result;
        }

        private Color ColorFor(Object asset)
        {
            var kind = AssetDependencyScanner.KindOf(asset);
            var manageable =
                kind == AssetKind.Material || kind == AssetKind.Texture ||
                kind == AssetKind.Animator || kind == AssetKind.Clip;
            if (!manageable) return NeutralColor;

            if (!IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(asset))) return SharedColor;
            return _foreignCopies.Contains(asset) ? ConflictColor : IsolatedColor;
        }

        private static void AddPorts(Node node)
        {
            var input = node.InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(bool));
            input.portName = string.Empty;
            node.inputContainer.Add(input);
            var output = node.InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(bool));
            output.portName = string.Empty;
            node.outputContainer.Add(output);
            node.RefreshExpandedState();
            node.RefreshPorts();
        }

        private void Connect(Node from, Node to)
        {
            if (!_edges.TryGetValue(from, out var targets))
            {
                targets = new HashSet<Node>();
                _edges[from] = targets;
            }
            if (!targets.Add(to)) return;

            var output = (Port)from.outputContainer[0];
            var input = (Port)to.inputContainer[0];
            var edge = output.ConnectTo(input);
            edge.capabilities &= ~(Capabilities.Deletable | Capabilities.Selectable);
            AddElement(edge);
        }
    }
}
