using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class ReadingPaneView : UserControl
{
    private bool _recipientsExpanded;

    public ReadingPaneView()
    {
        InitializeComponent();
        // Another mail: one line again. A new width can cut the lines (or no longer): the hint follows.
        DataContextChanged += (_, _) => ShowRecipients(expanded: false);
        RecipientLines.SizeChanged += (_, _) => UpdateRecipientsHint();
        PreviewToolbar.SizeChanged += (_, _) => ArrangePreviewToolbar();
        PreviewButtons.SizeChanged += (_, _) => ArrangePreviewToolbar();
        ToLine.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateRecipientsHint, Avalonia.Threading.DispatcherPriority.Background);
            }
        };
    }

    private void OnRecipientsTapped(object? sender, TappedEventArgs e)
    {
        if (_recipientsExpanded || IsCut(ToLine) || IsCut(CcLine))
        {
            ShowRecipients(!_recipientsExpanded);
            e.Handled = true;
        }
    }

    private void ShowRecipients(bool expanded)
    {
        _recipientsExpanded = expanded;
        foreach (var line in new[] { ToLine, CcLine })
        {
            line.TextWrapping = expanded ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap;
            line.TextTrimming = expanded ? Avalonia.Media.TextTrimming.None : Avalonia.Media.TextTrimming.CharacterEllipsis;
        }

        UpdateRecipientsHint();
    }

    private void UpdateRecipientsHint()
    {
        var clickable = _recipientsExpanded || IsCut(ToLine) || IsCut(CcLine);
        RecipientLines.Cursor = clickable ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        ToolTip.SetTip(RecipientLines, !clickable ? null
            : _recipientsExpanded ? Neruna.Core.Localization.Texts.T("Klicken für eine Zeile")
            : Neruna.Core.Localization.Texts.T("Klicken für alle Empfänger"));
    }

    /// <summary>The line shows less than its text (cut off with "…").</summary>
    private static bool IsCut(TextBlock line)
    {
        if (!line.IsVisible || string.IsNullOrEmpty(line.Text) || line.Bounds.Width <= 0 || line.TextWrapping != Avalonia.Media.TextWrapping.NoWrap)
        {
            return false;
        }

        using var full = new Avalonia.Media.TextFormatting.TextLayout(line.Text, new Avalonia.Media.Typeface(line.FontFamily, line.FontStyle, line.FontWeight),
            line.FontSize, null);
        return full.WidthIncludingTrailingWhitespace > line.Bounds.Width + 0.5;
    }

    private async void OnCopyHeaders(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ReadingPaneViewModel pane && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(pane.HeadersText);
        }
    }

    // Placing a stamp or text: a preview of it follows the mouse, at the size it will have on the page (scaled like
    // the page is shown) – placing by eye instead of by a cross-hair. The stamp is centred on the mouse, text starts there.
    private void OnPreviewPageMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Panel { Children: [Image { DataContext: PreviewPage page } image, Canvas canvas] })
        {
            return;
        }

        var preview = (DataContext as ReadingPaneViewModel)?.Preview;
        if (preview is not { IsStamping: true } || image.Bounds.Width <= 0)
        {
            HideGhost(image, canvas);
            return;
        }

        // An image attachment has no points: as on an A4 page.
        var scale = image.Bounds.Width / (page.PointWidth > 0 ? page.PointWidth : 595);
        var stamp = preview.IsAddingText ? null : preview.ActiveStamp;
        var key = stamp is { } s
            ? $"stamp|{s.Id}|{string.Join('|', preview.PreviewLines(s))}|{scale:F3}"
            : $"text|{preview.TextContent}|{preview.TextSize}|{preview.TextColor}|{scale:F3}";
        if (!string.Equals(canvas.Tag as string, key, StringComparison.Ordinal))
        {
            canvas.Children.Clear();
            canvas.Tag = key;
            Control? ghost = stamp is not null ? StampGhost(stamp, preview.PreviewLines(stamp), scale)
                : string.IsNullOrWhiteSpace(preview.TextContent) ? null
                : TextGhost(preview.TextContent, preview.TextSize, preview.TextColor, scale);
            if (ghost is not null)
            {
                ghost.Measure(Avalonia.Size.Infinity);
                canvas.Children.Add(ghost);
            }
        }

        var point = e.GetPosition(image);
        if (canvas.Children is [Control shown])
        {
            var size = shown.DesiredSize;
            Canvas.SetLeft(shown, stamp is null ? point.X : point.X - (size.Width / 2));
            Canvas.SetTop(shown, stamp is null ? point.Y : point.Y - (size.Height / 2));
            image.Cursor = NoCursor;
        }
        else
        {
            // Text not typed yet: where it will start.
            image.Cursor = TextCursor;
        }
    }

    private void OnPreviewPageExited(object? sender, PointerEventArgs e)
    {
        if (sender is Panel { Children: [Image image, Canvas canvas] })
        {
            HideGhost(image, canvas);
        }
    }

    private static void HideGhost(Image image, Canvas canvas)
    {
        canvas.Children.Clear();
        canvas.Tag = null;
        image.Cursor = null;
    }

    // Frame, title in capitals, lines below – the measures PdfPages.DrawStamp uses.
    private static Border StampGhost(Neruna.Core.Mail.PdfStamp stamp, IReadOnlyList<string> lines, double scale)
    {
        var brush = Avalonia.Media.Brush.Parse(stamp.Color);
        var content = new StackPanel { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = stamp.Text.ToUpper(System.Globalization.CultureInfo.CurrentCulture),
            FontFamily = GhostFont,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            FontSize = Neruna.Core.Mail.PdfPages.TitleSize * scale,
            LineHeight = Neruna.Core.Mail.PdfPages.TitleSize * scale,
            Foreground = brush,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        });
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            content.Children.Add(new TextBlock
            {
                Text = line,
                FontFamily = GhostFont,
                FontSize = Neruna.Core.Mail.PdfPages.LineSize * scale,
                LineHeight = Neruna.Core.Mail.PdfPages.LineHeight * scale,
                Foreground = brush,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            });
        }

        return new Border
        {
            BorderBrush = brush,
            BorderThickness = new Avalonia.Thickness(1.8 * scale),
            Padding = new Avalonia.Thickness((Neruna.Core.Mail.PdfPages.Padding + 4) * scale, Neruna.Core.Mail.PdfPages.Padding * scale),
            MinWidth = Neruna.Core.Mail.PdfPages.MinStampWidth * scale,
            Opacity = 0.7,
            Child = content,
        };
    }

    private static TextBlock TextGhost(string text, float size, string color, double scale) => new()
    {
        Text = text.Replace("\r\n", "\n", StringComparison.Ordinal),
        FontFamily = GhostFont,
        FontSize = size * scale,
        LineHeight = size * 1.25 * scale,
        Foreground = Avalonia.Media.Brush.Parse(color),
        Opacity = 0.7,
    };

    // Made once: a cursor holds a native handle.
    private static readonly Cursor NoCursor = new(StandardCursorType.None);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    // The stamp is written in Helvetica (PDF base font); on screen the closest the system has.
    private static readonly Avalonia.Media.FontFamily GhostFont = new("Helvetica, Arial, Liberation Sans, sans-serif");

    // Placing a stamp: where on the page (as share of its shown size) the click was.
    private async void OnPreviewPagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Image { DataContext: PreviewPage page } image || DataContext is not ReadingPaneViewModel { Preview: { IsStamping: true } preview }
            || image.Bounds.Width <= 0 || image.Bounds.Height <= 0)
        {
            return;
        }

        var point = e.GetPosition(image);
        e.Handled = true;
        await preview.PlaceAsync(page, point.X / image.Bounds.Width, point.Y / image.Bounds.Height);
    }

    // Attachment preview: the buttons stay right of the file name while there is room (the name keeps at least ~200 px),
    // otherwise they move onto their own line below – instead of running past the window's edge.
    private void ArrangePreviewToolbar()
    {
        var available = PreviewToolbar.Bounds.Width;
        if (available <= 0)
        {
            return;
        }

        var buttons = 0.0;
        foreach (var child in PreviewButtons.Children.Where(c => c.IsVisible))
        {
            buttons += child.DesiredSize.Width + 4;
        }

        var wrap = PreviewBack.Bounds.Width + 200 + buttons > available;
        if (wrap == (Grid.GetRow(PreviewButtons) == 1))
        {
            return;
        }

        Grid.SetRow(PreviewButtons, wrap ? 1 : 0);
        Grid.SetColumn(PreviewButtons, wrap ? 0 : 2);
        Grid.SetColumnSpan(PreviewButtons, wrap ? 3 : 1);
        PreviewButtons.Margin = wrap ? new Thickness(0, 6, 0, 0) : default;
    }
}
