using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Neruna.Core.Mail;

namespace Neruna.Desktop.Controls;

/// <summary>
/// A plain text to read (notes of a task, description of an event) with its links clickable right where they
/// stand; the text stays selectable for copying. A click opens a link only when nothing was selected by it.
/// </summary>
internal sealed class LinkedText : SelectableTextBlock
{
    public static readonly StyledProperty<string?> LinkedProperty = AvaloniaProperty.Register<LinkedText, string?>(nameof(Linked));

    private IReadOnlyList<(int Start, int Length, Uri Uri)> _links = [];

    public LinkedText()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>The text; its web addresses become links.</summary>
    public string? Linked
    {
        get => GetValue(LinkedProperty);
        set => SetValue(LinkedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LinkedProperty)
        {
            Build(Linked ?? string.Empty);
        }
    }

    private void Build(string text)
    {
        _links = MessageContent.FindLinkRanges(text, otherSchemes: true);
        var inlines = new InlineCollection();
        var at = 0;
        foreach (var (start, length, _) in _links)
        {
            if (start > at)
            {
                inlines.Add(new Run(text[at..start]));
            }

            var link = new Run(text.Substring(start, length)) { TextDecorations = Avalonia.Media.TextDecorations.Underline };
            link.Bind(Run.ForegroundProperty, this.GetResourceObservable("HyperlinkButtonForeground"));
            inlines.Add(link);
            at = start + length;
        }

        if (at < text.Length)
        {
            inlines.Add(new Run(text[at..]));
        }

        Inlines = inlines;
    }

    private Uri? LinkAt(Point point)
    {
        if (_links.Count == 0)
        {
            return null;
        }

        var hit = TextLayout.HitTestPoint(point - new Point(Padding.Left, Padding.Top));
        if (!hit.IsInside)
        {
            return null;
        }

        var position = hit.TextPosition;
        foreach (var (start, length, uri) in _links)
        {
            if (position >= start && position < start + length)
            {
                return uri;
            }
        }

        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = LinkAt(e.GetPosition(this)) is null ? null : new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left && SelectionStart == SelectionEnd
            && LinkAt(e.GetPosition(this)) is { } uri)
        {
            _ = Infrastructure.LinkOpener.OpenAsync(this, uri);
            e.Handled = true;
        }
    }
}
