using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Neruna.Desktop.Controls;

/// <summary>
/// A toolbar button: icon on top, the text below it (or only the icon, with the text as tooltip). The tooltip always
/// shows the text and, if given, the keyboard shortcut – or <see cref="Hint"/> when set.
/// </summary>
internal sealed class ToolButton : Button
{
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<ToolButton, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<ToolButton, string?>(nameof(Label));

    public static readonly StyledProperty<bool> ShowLabelProperty = AvaloniaProperty.Register<ToolButton, bool>(nameof(ShowLabel), true);

    public static readonly StyledProperty<string?> ShortcutProperty = AvaloniaProperty.Register<ToolButton, string?>(nameof(Shortcut));

    public static readonly StyledProperty<string?> HintProperty = AvaloniaProperty.Register<ToolButton, string?>(nameof(Hint));

    public ToolButton()
    {
        Classes.Add("command");
        Classes.Add("tool");
        ToolContent.Build(this);
        ToolContent.UpdateTip(this, Label, Shortcut, Hint);
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

    /// <summary>A longer tooltip than the label (e.g. why the button is disabled).</summary>
    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty || change.Property == ShortcutProperty || change.Property == HintProperty)
        {
            ToolContent.UpdateTip(this, Label, Shortcut, Hint);
        }
        else if (change.Property == ShowLabelProperty)
        {
            ToolContent.UpdateSize(this, ShowLabel);
        }
    }
}

/// <summary>An on/off toolbar button (Signieren, Verschlüsseln) in the look of <see cref="ToolButton"/>.</summary>
internal sealed class ToolToggleButton : ToggleButton
{
    public static readonly StyledProperty<Geometry?> IconProperty = ToolButton.IconProperty.AddOwner<ToolToggleButton>();

    public static readonly StyledProperty<string?> LabelProperty = ToolButton.LabelProperty.AddOwner<ToolToggleButton>();

    public static readonly StyledProperty<bool> ShowLabelProperty = ToolButton.ShowLabelProperty.AddOwner<ToolToggleButton>();

    public static readonly StyledProperty<string?> HintProperty = ToolButton.HintProperty.AddOwner<ToolToggleButton>();

    public ToolToggleButton()
    {
        Classes.Add("tool");
        ToolContent.Build(this);
        ToolContent.UpdateTip(this, Label, null, Hint);
    }

    protected override Type StyleKeyOverride => typeof(ToggleButton);

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

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty || change.Property == HintProperty)
        {
            ToolContent.UpdateTip(this, Label, null, Hint);
        }
        else if (change.Property == ShowLabelProperty)
        {
            ToolContent.UpdateSize(this, ShowLabel);
        }
    }
}

internal static class ToolContent
{
    /// <summary>Icon above, label below (bound to the button's Icon, Label and ShowLabel).</summary>
    public static void Build(ContentControl button)
    {
        var icon = new PathIcon { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0) };
        icon.Bind(PathIcon.DataProperty, new Binding("Icon") { Source = button });
        var label = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        label.Bind(TextBlock.TextProperty, new Binding("Label") { Source = button });
        label.Bind(Visual.IsVisibleProperty, new Binding("ShowLabel") { Source = button });
        button.Content = new StackPanel { Children = { icon, label } };
    }

    public static void UpdateSize(TemplatedControl button, bool showLabel)
    {
        button.MinWidth = showLabel ? 64 : 0;
        button.Padding = showLabel ? new Thickness(6, 4) : new Thickness(8, 6);
    }

    public static void UpdateTip(Control button, string? label, string? shortcut, string? hint) =>
        ToolTip.SetTip(button, hint is { Length: > 0 } ? hint : shortcut is { Length: > 0 } key ? $"{label} ({key})" : label);
}
