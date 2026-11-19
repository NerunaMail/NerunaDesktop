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
        ToLine.SizeChanged += (_, _) => PostRecipientsCheck();
        CcLine.SizeChanged += (_, _) => PostRecipientsCheck();
        ToLine.LayoutUpdated += (_, _) => PostRecipientsCheck();
        PreviewToolbar.SizeChanged += (_, _) => ArrangePreviewToolbar();
        PreviewButtons.SizeChanged += (_, _) => ArrangePreviewToolbar();
        // Zoom of the preview: 100 % follows the pane's width; Ctrl + mouse wheel before the scroller scrolls.
        PreviewScroller.SizeChanged += (_, _) => SetPreviewWidth();
        PreviewScroller.DataContextChanged += (_, _) => SetPreviewWidth();
        PreviewScroller.AddHandler(PointerWheelChangedEvent, OnPreviewWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>A page's width: at 100 % the pane's (less the margins), a smaller picture its own; times the zoom.</summary>
    public static Avalonia.Data.Converters.FuncMultiValueConverter<object?, double> PageWidth { get; } = new(values =>
    {
        var list = values.ToList();
        return list is [Avalonia.Media.Imaging.Bitmap image, double view, double zoom] && view > 32
            ? Math.Max(1, Math.Min(view - 32, image.Size.Width) * zoom)
            : double.NaN;
    });

    private void SetPreviewWidth()
    {
        if (PreviewScroller.DataContext is AttachmentPreviewViewModel preview && PreviewScroller.Bounds.Width > 0)
        {
            preview.ViewWidth = PreviewScroller.Bounds.Width;
        }
    }

    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Delta.Y != 0 && PreviewScroller.DataContext is AttachmentPreviewViewModel preview)
        {
            e.Handled = true;
            if (e.Delta.Y > 0)
            {
                preview.ZoomInCommand.Execute(null);
            }
            else
            {
                preview.ZoomOutCommand.Execute(null);
            }
        }
    }

    // ── Recipients: one line per kind (the rest behind «+N»), expanded all; each name has its menu. ──

    private const double RecipientLineHeight = 18;
    private bool _recipientsCheckPosted;

    private void OnRecipientsMore(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowRecipients(!_recipientsExpanded);

    private void ShowRecipients(bool expanded)
    {
        _recipientsExpanded = expanded;
        ToLine.MaxHeight = CcLine.MaxHeight = expanded ? double.PositiveInfinity : RecipientLineHeight;
        PostRecipientsCheck();
    }

    private void PostRecipientsCheck()
    {
        if (_recipientsCheckPosted)
        {
            return;
        }

        _recipientsCheckPosted = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _recipientsCheckPosted = false;
            UpdateMore(ToLine, ToMore);
            UpdateMore(CcLine, CcMore);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // «+N» when names are on lines below the first (hidden), «Weniger» when expanded and there is more than one line.
    private void UpdateMore(ItemsControl line, Button more)
    {
        var items = line.GetRealizedContainers().Where(c => c.IsVisible).ToList();
        var below = items.Count(c => c.Bounds.Top > RecipientLineHeight / 2);
        more.IsVisible = below > 0;
        var text = _recipientsExpanded ? Neruna.Core.Localization.Texts.T("Weniger") : Neruna.Core.Localization.Texts.F("+{0} weitere", below);
        if (!Equals(more.Content, text))
        {
            more.Content = text;
        }

        ToolTip.SetTip(more, _recipientsExpanded ? Neruna.Core.Localization.Texts.T("Klicken für eine Zeile") : Neruna.Core.Localization.Texts.T("Klicken für alle Empfänger"));
    }

    // A click on a name: the same menu as a right-click.
    private void OnRecipientClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { ContextFlyout: { } menu } button)
        {
            menu.ShowAt(button);
        }
    }

    private async void OnCopyRecipient(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RecipientItem recipient } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(recipient.Address);
        }
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
