using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class ReadingPaneView : UserControl
{
    public ReadingPaneView()
    {
        InitializeComponent();
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
            image.Cursor = new Cursor(StandardCursorType.None);
        }
        else
        {
            // Text not typed yet: where it will start.
            image.Cursor = new Cursor(StandardCursorType.Ibeam);
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
}
