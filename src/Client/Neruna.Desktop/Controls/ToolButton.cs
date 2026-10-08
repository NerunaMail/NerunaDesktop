using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Neruna.Desktop.Controls;

/// <summary>
/// A toolbar button: icon on top, the text below it (or only the icon, with the text as tooltip). The tooltip always
/// shows the text and, if given, the keyboard shortcut.
/// </summary>
internal sealed class ToolButton : Button
{
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<ToolButton, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<ToolButton, string?>(nameof(Label));

    public static readonly StyledProperty<bool> ShowLabelProperty = AvaloniaProperty.Register<ToolButton, bool>(nameof(ShowLabel), true);

    public static readonly StyledProperty<string?> ShortcutProperty = AvaloniaProperty.Register<ToolButton, string?>(nameof(Shortcut));

    public ToolButton()
    {
        Classes.Add("command");
        Classes.Add("tool");
        var icon = new PathIcon { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0) };
        icon.Bind(PathIcon.DataProperty, new Binding(nameof(Icon)) { Source = this });
        var label = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        label.Bind(TextBlock.TextProperty, new Binding(nameof(Label)) { Source = this });
        label.Bind(IsVisibleProperty, new Binding(nameof(ShowLabel)) { Source = this });
        Content = new StackPanel { Children = { icon, label } };
        UpdateTip();
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool ShowLabel
    {
        get => GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    public string? Shortcut
    {
        get => GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty || change.Property == ShortcutProperty)
        {
            UpdateTip();
        }
        else if (change.Property == ShowLabelProperty)
        {
            MinWidth = ShowLabel ? 64 : 0;
            Padding = ShowLabel ? new Thickness(6, 4) : new Thickness(8, 6);
        }
    }

    private void UpdateTip() => ToolTip.SetTip(this, Shortcut is { Length: > 0 } key ? $"{Label} ({key})" : Label);
}
