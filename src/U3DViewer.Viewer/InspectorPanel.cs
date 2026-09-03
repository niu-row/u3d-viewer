using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using U3DViewer.Protocol;

namespace U3DViewer.Viewer;

internal sealed class InspectorPanel : Border
{
    private readonly StackPanel _content;
    private GameObjectInfo? _current;

    public InspectorPanel()
    {
        BorderBrush = Brushes.Gray;
        BorderThickness = new Thickness(1, 0, 0, 0);

        _content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 6
        };

        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        panel.Children.Add(new TextBlock
        {
            Text = Localization.T("main.inspector"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 10, 10, 8)
        });

        var scroll = new ScrollViewer { Content = _content };
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);
        Child = panel;

        Localization.LanguageChanged += RefreshLanguage;
        Show(null);
    }

    public void Show(GameObjectInfo? gameObject)
    {
        _current = gameObject;
        _content.Children.Clear();

        if (gameObject is null)
        {
            AddLine(Localization.T("main.selectObject"));
            return;
        }

        AddHeading(gameObject.Name, 20);
        AddLine(Localization.Translate($"Instance ID: {gameObject.InstanceId}"));
        AddLine(Localization.Translate($"Active: {gameObject.ActiveInHierarchy}  (self: {gameObject.ActiveSelf})"));
        AddLine(Localization.Translate($"Children: {gameObject.ChildCount}"));
        AddLine(Localization.Translate($"Layer: {gameObject.Layer}"));
        AddLine(Localization.Translate($"Tag: {(string.IsNullOrWhiteSpace(gameObject.Tag) ? "<none>" : gameObject.Tag)}"));

        AddHeading(Localization.T("main.transform"), 15);
        AddLine(Localization.Translate($"Position:       {FormatVector(gameObject.Transform.Position)}"));
        AddLine(Localization.Translate($"Local Position: {FormatVector(gameObject.Transform.LocalPosition)}"));
        AddLine(Localization.Translate($"Euler Angles:   {FormatVector(gameObject.Transform.EulerAngles)}"));
        AddLine(Localization.Translate($"Local Scale:    {FormatVector(gameObject.Transform.LocalScale)}"));

        AddHeading(Localization.Translate($"Components ({gameObject.Components.Length})"), 15);
        if (gameObject.Components.Length == 0)
        {
            AddLine(Localization.T("main.none"));
        }
        else
        {
            foreach (var component in gameObject.Components)
            {
                AddLine(component);
            }
        }
    }

    public void Shutdown()
    {
        Localization.LanguageChanged -= RefreshLanguage;
    }

    private void RefreshLanguage() => Show(_current);

    private void AddHeading(string text, double size)
    {
        _content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 8, 0, 2)
        });
    }

    private void AddLine(string text)
    {
        _content.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        });
    }

    private static string FormatVector(Vector3Info value) =>
        $"({value.X:0.###}, {value.Y:0.###}, {value.Z:0.###})";
}
