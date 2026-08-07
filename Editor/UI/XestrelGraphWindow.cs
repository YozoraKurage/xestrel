using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Xestrel.Isolation;
using Xestrel.Runtime;

namespace Xestrel.UI
{
    /// <summary>
    /// Read-only visualization of an avatar's isolation state: avatar → materials →
    /// textures, and avatar → descriptor controllers → clips, colored by isolation
    /// status. Copies that another workspace's manifest also tracks (a duplicated
    /// avatar, possibly in an unloaded scene) are flagged as conflicts. Double-click a
    /// node to ping its asset.
    /// </summary>
    internal sealed class XestrelGraphWindow : EditorWindow
    {
        [SerializeField] private GameObject _avatar;
        private XestrelGraphView _graph;
        private ObjectField _avatarField;

        [MenuItem("Window/Xestrel/Isolation Graph")]
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

            var toolbar = new Toolbar();
            _avatarField = new ObjectField { objectType = typeof(GameObject), allowSceneObjects = true };
            _avatarField.style.minWidth = 220;
            _avatarField.SetValueWithoutNotify(_avatar);
            _avatarField.RegisterValueChangedCallback(evt =>
            {
                _avatar = evt.newValue as GameObject;
                RebuildGraph();
            });
            toolbar.Add(_avatarField);
            toolbar.Add(new ToolbarButton(RebuildGraph) { text = "Refresh" });
            toolbar.Add(MakeLegend("● shared", XestrelGraphView.SharedColor));
            toolbar.Add(MakeLegend("● isolated", XestrelGraphView.IsolatedColor));
            toolbar.Add(MakeLegend("● shared with another workspace", XestrelGraphView.ConflictColor));
            rootVisualElement.Add(toolbar);

            _graph = new XestrelGraphView();
            _graph.style.flexGrow = 1;
            rootVisualElement.Add(_graph);
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

        private void RebuildGraph()
        {
            if (_graph != null) _graph.Rebuild(_avatar);
        }
    }

    internal sealed class XestrelGraphView : GraphView
    {
        public static readonly Color SharedColor = new Color(0.85f, 0.55f, 0.15f);
        public static readonly Color IsolatedColor = new Color(0.30f, 0.65f, 0.35f);
        public static readonly Color ConflictColor = new Color(0.85f, 0.25f, 0.25f);
        private static readonly Color AvatarColor = new Color(0.35f, 0.40f, 0.55f);

        private const float ColumnWidth = 300f;
        private const float RowHeight = 100f;
        private const float NodeWidth = 220f;

        public XestrelGraphView()
        {
            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
        }

        public void Rebuild(GameObject avatar)
        {
            var stale = new List<GraphElement>();
            graphElements.ForEach(e => stale.Add(e));
            DeleteElements(stale);
            if (avatar == null) return;

            var state = avatar.GetComponent<XestrelMaterialIsolation>();
            var foreignCopies = CollectForeignCopies(state);

            var avatarNode = MakeNode(avatar.name, avatar, AvatarColor, 0, 0f);

            float matY = 0f, texY = 0f;
            var texNodes = new Dictionary<Texture, Node>();
            foreach (var mat in WorkspaceAudit.CollectRendererMaterials(avatar))
            {
                var matNode = MakeNode(mat.name, mat, ColorFor(mat, foreignCopies), 1, matY);
                matY += RowHeight;
                Connect(avatarNode, matNode);
                foreach (var tex in WorkspaceAudit.CollectMaterialTextures(mat))
                {
                    if (!texNodes.TryGetValue(tex, out var texNode))
                    {
                        texNode = MakeNode(tex.name, tex, ColorFor(tex, foreignCopies), 2, texY);
                        texY += RowHeight;
                        texNodes[tex] = texNode;
                    }
                    Connect(matNode, texNode);
                }
            }

            float ctrlY = matY + RowHeight * 0.5f;
            float clipY = texY + RowHeight * 0.5f;
            var clipNodes = new Dictionary<AnimationClip, Node>();
            foreach (var ctrl in WorkspaceAudit.CollectDescriptorControllers(avatar))
            {
                var ctrlNode = MakeNode(ctrl.name, ctrl, ColorFor(ctrl, foreignCopies), 1, ctrlY);
                ctrlY += RowHeight;
                Connect(avatarNode, ctrlNode);
                foreach (var clip in WorkspaceAudit.CollectControllerClips(ctrl))
                {
                    if (!clipNodes.TryGetValue(clip, out var clipNode))
                    {
                        clipNode = MakeNode(clip.name, clip, ColorFor(clip, foreignCopies), 2, clipY);
                        clipY += RowHeight;
                        clipNodes[clip] = clipNode;
                    }
                    Connect(ctrlNode, clipNode);
                }
            }
        }

        // Copies tracked by any other workspace's manifest. Manifests are assets, so
        // this sees duplicated avatars even when their scene is not loaded.
        private static HashSet<Object> CollectForeignCopies(XestrelMaterialIsolation state)
        {
            var result = new HashSet<Object>();
            var own = state != null ? state.workspaceManifest : null;
            foreach (var manifest in WorkspaceManifests.FindAll())
            {
                if (manifest == null || manifest == own) continue;
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

        private static Color ColorFor(Object asset, HashSet<Object> foreignCopies)
        {
            var isolated = IsolationPaths.IsUnderIsolationRoot(AssetDatabase.GetAssetPath(asset));
            if (!isolated) return SharedColor;
            return foreignCopies.Contains(asset) ? ConflictColor : IsolatedColor;
        }

        private Node MakeNode(string title, Object pingTarget, Color color, int column, float y)
        {
            var node = new Node { title = title };
            node.capabilities &= ~(Capabilities.Deletable | Capabilities.Renamable | Capabilities.Copiable);
            node.titleContainer.style.backgroundColor = color;
            node.SetPosition(new Rect(40f + column * ColumnWidth, 40f + y, NodeWidth, 80f));
            if (pingTarget != null)
            {
                node.RegisterCallback<MouseDownEvent>(evt =>
                {
                    if (evt.clickCount == 2)
                    {
                        EditorGUIUtility.PingObject(pingTarget);
                        Selection.activeObject = pingTarget;
                    }
                });
            }

            var input = node.InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(bool));
            input.portName = string.Empty;
            node.inputContainer.Add(input);
            var output = node.InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(bool));
            output.portName = string.Empty;
            node.outputContainer.Add(output);
            node.RefreshExpandedState();
            node.RefreshPorts();
            AddElement(node);
            return node;
        }

        private void Connect(Node from, Node to)
        {
            var output = (Port)from.outputContainer[0];
            var input = (Port)to.inputContainer[0];
            var edge = output.ConnectTo(input);
            edge.capabilities &= ~(Capabilities.Deletable | Capabilities.Selectable);
            AddElement(edge);
        }
    }
}
