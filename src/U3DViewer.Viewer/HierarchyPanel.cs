using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class HierarchyPanel : Border
{
    private readonly ObservableCollection<HierarchyNode> _rootNodes = new();
    private readonly HashSet<int> _expandedInstanceIds = new();
    private readonly HashSet<long> _expandedSceneKeys = new();
    private readonly TreeView _tree;

    private HierarchyNode? _selectedNode;

    public HierarchyPanel()
    {
        BorderBrush = Brushes.Gray;
        BorderThickness = new Thickness(0, 0, 1, 0);

        _tree = new TreeView
        {
            ItemsSource = _rootNodes,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncTreeDataTemplate<HierarchyNode>(
                BuildHierarchyHeader,
                node => node.Children)
        };
        _tree.SelectionChanged += OnSelectionChanged;
        _tree.AddHandler(TreeViewItem.ExpandedEvent, OnExpanded);
        _tree.AddHandler(TreeViewItem.CollapsedEvent, OnCollapsed);

        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        panel.Children.Add(new TextBlock
        {
            Text = Localization.T("main.hierarchy"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 10, 10, 8)
        });

        var scroll = new ScrollViewer { Content = _tree };
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);
        Child = panel;

        Localization.LanguageChanged += RefreshLabels;
    }

    public event Action<int, GameObjectInfo?>? SelectionChanged;
    public event Action<int, bool>? ExpansionChanged;
    public event Action<int, string, bool>? SceneExpansionChanged;

    public int? SelectedInstanceId => _selectedNode?.InstanceId;
    public GameObjectInfo? SelectedGameObject => _selectedNode?.GameObject;

    public void ApplyScenes(IReadOnlyList<SceneInfo> scenes)
    {
        var desiredKeys = new HashSet<string>();
        var existingScenes = new Dictionary<string, HierarchyNode>(StringComparer.Ordinal);
        foreach (var existing in _rootNodes)
        {
            existingScenes[existing.Key] = existing;
        }

        for (var index = 0; index < scenes.Count; index++)
        {
            var scene = scenes[index];
            var key = $"scene:{scene.BuildIndex}:{scene.Name}";
            var sceneKey = ViewerCommandCodec.BuildSceneKey(scene.BuildIndex, scene.Name);
            desiredKeys.Add(key);

            if (!existingScenes.TryGetValue(key, out var node))
            {
                node = HierarchyNode.Scene(key, scene.Name, scene.BuildIndex);
                _rootNodes.Insert(Math.Min(index, _rootNodes.Count), node);
                existingScenes[key] = node;
            }
            else if (index < _rootNodes.Count && !ReferenceEquals(_rootNodes[index], node))
            {
                var currentIndex = _rootNodes.IndexOf(node);
                if (currentIndex >= 0 && currentIndex != index)
                {
                    _rootNodes.Move(currentIndex, index);
                }
            }

            node.SceneName = scene.Name;
            node.SceneBuildIndex = scene.BuildIndex;
            UpdateLabel(node);

            if (scene.Roots.Length > 0)
            {
                SyncGameObjects(node.Children, scene.Roots);
            }
            else if (scene.IsLoaded && !_expandedSceneKeys.Contains(sceneKey))
            {
                // Keep a tiny placeholder so the TreeView exposes an expansion affordance.
                // The Agent will not call GetRootGameObjects until the user expands this Scene.
                EnsurePlaceholder(node);
            }
            else
            {
                node.Children.Clear();
            }
        }

        for (var index = _rootNodes.Count - 1; index >= 0; index--)
        {
            if (!desiredKeys.Contains(_rootNodes[index].Key))
            {
                var removed = _rootNodes[index];
                _expandedSceneKeys.Remove(ViewerCommandCodec.BuildSceneKey(removed.SceneBuildIndex, removed.SceneName));
                _rootNodes.RemoveAt(index);
            }
        }

        if (_selectedNode?.InstanceId is not int selectedId)
        {
            return;
        }

        var refreshed = FindByInstanceId(selectedId);
        if (refreshed is null)
        {
            _selectedNode = null;
            _tree.SelectedItem = null;
            SelectionChanged?.Invoke(0, null);
        }
        else if (!ReferenceEquals(refreshed, _selectedNode))
        {
            _selectedNode = refreshed;
            _tree.SelectedItem = refreshed;
        }
    }

    public void ResetConnectionState()
    {
        _expandedInstanceIds.Clear();
        _expandedSceneKeys.Clear();
    }

    public void Shutdown()
    {
        Localization.LanguageChanged -= RefreshLabels;
    }

    private void SyncGameObjects(ObservableCollection<HierarchyNode> target, IReadOnlyList<GameObjectInfo> objects)
    {
        if (objects.Count > 0)
        {
            RemovePlaceholders(target);
        }

        var existingById = new Dictionary<int, HierarchyNode>();
        foreach (var existing in target)
        {
            if (!existing.IsPlaceholder && existing.InstanceId is int instanceId)
            {
                existingById[instanceId] = existing;
            }
        }

        var desiredIds = new HashSet<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            var gameObject = objects[index];
            desiredIds.Add(gameObject.InstanceId);

            if (!existingById.TryGetValue(gameObject.InstanceId, out var node))
            {
                node = HierarchyNode.FromGameObject(gameObject);
                target.Insert(Math.Min(index, target.Count), node);
                existingById[gameObject.InstanceId] = node;
            }
            else if (index < target.Count && !ReferenceEquals(target[index], node))
            {
                var currentIndex = target.IndexOf(node);
                if (currentIndex >= 0 && currentIndex != index)
                {
                    target.Move(currentIndex, index);
                }
            }

            node.GameObject = gameObject;
            UpdateLabel(node);

            if (gameObject.Children.Length > 0)
            {
                SyncGameObjects(node.Children, gameObject.Children);
            }
            else if (gameObject.ChildCount > 0)
            {
                EnsurePlaceholder(node);
            }
            else
            {
                node.Children.Clear();
            }
        }

        for (var index = target.Count - 1; index >= 0; index--)
        {
            var item = target[index];
            if (item.IsPlaceholder)
            {
                continue;
            }

            if (item.InstanceId is int id && !desiredIds.Contains(id))
            {
                target.RemoveAt(index);
            }
        }
    }

    private static void RemovePlaceholders(ObservableCollection<HierarchyNode> children)
    {
        for (var index = children.Count - 1; index >= 0; index--)
        {
            if (children[index].IsPlaceholder)
            {
                children.RemoveAt(index);
            }
        }
    }

    private static void EnsurePlaceholder(HierarchyNode node)
    {
        if (node.Children.Any(child => !child.IsPlaceholder))
        {
            return;
        }

        if (node.Children.Count == 1 && node.Children[0].IsPlaceholder)
        {
            UpdateLabel(node.Children[0]);
            return;
        }

        node.Children.Clear();
        node.Children.Add(HierarchyNode.Placeholder());
    }

    private void OnExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem item || item.DataContext is not HierarchyNode node || node.IsPlaceholder)
        {
            return;
        }

        if (node.GameObject is null)
        {
            var sceneKey = ViewerCommandCodec.BuildSceneKey(node.SceneBuildIndex, node.SceneName);
            if (_expandedSceneKeys.Add(sceneKey))
            {
                SceneExpansionChanged?.Invoke(node.SceneBuildIndex, node.SceneName, true);
            }
            return;
        }

        if (node.InstanceId is int instanceId && _expandedInstanceIds.Add(instanceId))
        {
            ExpansionChanged?.Invoke(instanceId, true);
        }
    }

    private void OnCollapsed(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem item || item.DataContext is not HierarchyNode node || node.IsPlaceholder)
        {
            return;
        }

        CollapseBranch(node);
    }

    private void CollapseBranch(HierarchyNode node)
    {
        if (node.GameObject is null)
        {
            var sceneKey = ViewerCommandCodec.BuildSceneKey(node.SceneBuildIndex, node.SceneName);
            if (_expandedSceneKeys.Remove(sceneKey))
            {
                SceneExpansionChanged?.Invoke(node.SceneBuildIndex, node.SceneName, false);
            }
        }
        else if (node.InstanceId is int instanceId && _expandedInstanceIds.Remove(instanceId))
        {
            ExpansionChanged?.Invoke(instanceId, false);
        }

        foreach (var child in node.Children)
        {
            if (!child.IsPlaceholder)
            {
                CollapseBranch(child);
            }
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tree.SelectedItem is HierarchyNode node && !node.IsPlaceholder &&
            node.GameObject is not null && node.InstanceId is int instanceId)
        {
            _selectedNode = node;
            SelectionChanged?.Invoke(instanceId, node.GameObject);
            return;
        }

        _selectedNode = null;
        SelectionChanged?.Invoke(0, null);
    }

    private HierarchyNode? FindByInstanceId(int instanceId)
    {
        foreach (var root in _rootNodes)
        {
            var match = FindByInstanceId(root, instanceId);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static HierarchyNode? FindByInstanceId(HierarchyNode node, int instanceId)
    {
        if (node.InstanceId == instanceId)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (child.IsPlaceholder)
            {
                continue;
            }

            var match = FindByInstanceId(child, instanceId);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private void RefreshLabels()
    {
        foreach (var root in _rootNodes)
        {
            RefreshLabels(root);
        }
    }

    private static void RefreshLabels(HierarchyNode node)
    {
        UpdateLabel(node);
        foreach (var child in node.Children)
        {
            RefreshLabels(child);
        }
    }

    private static void UpdateLabel(HierarchyNode node)
    {
        if (node.IsPlaceholder)
        {
            node.Label = Localization.T("main.loading");
            return;
        }

        if (node.GameObject is { } gameObject)
        {
            node.Label = Localization.Translate(
                gameObject.ActiveInHierarchy ? gameObject.Name : $"{gameObject.Name} (inactive)");
            return;
        }

        node.Label = Localization.Translate($"Scene: {node.SceneName}  [build {node.SceneBuildIndex}]");
    }

    private static Control BuildHierarchyHeader(HierarchyNode node, INameScope _)
    {
        var text = new TextBlock();
        text.Classes.Add(Localization.SkipAutoTranslateClass);
        text.Bind(TextBlock.TextProperty, new Binding(nameof(HierarchyNode.Label)));
        return text;
    }

    private sealed class HierarchyNode : INotifyPropertyChanged
    {
        private string _label;

        private HierarchyNode(string key, int? instanceId, string label, bool isPlaceholder)
        {
            Key = key;
            InstanceId = instanceId;
            _label = label;
            IsPlaceholder = isPlaceholder;
        }

        public static HierarchyNode Scene(string key, string name, int buildIndex) =>
            new(key, null, string.Empty, false)
            {
                SceneName = name,
                SceneBuildIndex = buildIndex
            };

        public static HierarchyNode FromGameObject(GameObjectInfo gameObject) =>
            new($"go:{gameObject.InstanceId}", gameObject.InstanceId, string.Empty, false)
            {
                GameObject = gameObject
            };

        public static HierarchyNode Placeholder() =>
            new("placeholder", null, Localization.T("main.loading"), true);

        public string Key { get; }
        public int? InstanceId { get; }
        public bool IsPlaceholder { get; }
        public ObservableCollection<HierarchyNode> Children { get; } = new();
        public GameObjectInfo? GameObject { get; set; }
        public string SceneName { get; set; } = string.Empty;
        public int SceneBuildIndex { get; set; }

        public string Label
        {
            get => _label;
            set
            {
                if (_label == value)
                {
                    return;
                }

                _label = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
