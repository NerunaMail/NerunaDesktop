using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Controls;

/// <summary>
/// The web addresses in a text field (notes of a task, description of an event), clickable below it: the field itself
/// stays a plain text to edit. Hidden when there are none.
/// </summary>
internal sealed class TextLinks : UserControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<TextLinks, string?>(nameof(Text));

    private const int MaxLinks = 10;
    private const int MaxShown = 60;

    private readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 2 };

    public TextLinks()
    {
        var icon = new PathIcon { Width = 14, Height = 14, Margin = new Thickness(0, 4, 6, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable("IconLink"));
        icon.Bind(PathIcon.ForegroundProperty, icon.GetResourceObservable("SubtleTextBrush"));
        ToolTip.SetTip(icon, T("Links in diesem Text"));
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(_panel);
        Content = dock;
        IsVisible = false;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            Show(MessageContent.FindLinks(Text));
        }
    }

    private void Show(IReadOnlyList<Uri> links)
    {
        _panel.Children.Clear();
        foreach (var link in links.Take(MaxLinks))
        {
            var text = link.OriginalString;
            var shown = text.Length > MaxShown ? text[..(MaxShown - 1)] + "…" : text;
            var button = new HyperlinkButton { Content = shown, NavigateUri = link, Padding = new Thickness(0) };
            ToolTip.SetTip(button, text);
            _panel.Children.Add(button);
        }

        IsVisible = _panel.Children.Count > 0;
    }
}
