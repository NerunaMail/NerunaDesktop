using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

/// <summary>
/// A picture of a mail in its own window ("Bild vergrössern" in the reading pane's menu): the size can be changed, the
/// picture fits the window until zoomed. Replaces the web view's own magnifier, a small popup of fixed size.
/// </summary>
internal sealed partial class ImageViewerWindow : Window
{
    private static readonly double[] Steps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8];
    private readonly Bitmap? _bitmap;
    private double? _zoom; // null: fitted to the window

    public ImageViewerWindow()
    {
        InitializeComponent();
    }

    public ImageViewerWindow(Bitmap bitmap)
        : this()
    {
        _bitmap = bitmap;
        Picture.Source = bitmap;
        SizeText.Text = F("{0} × {1} Pixel", bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        Scroller.SizeChanged += (_, _) => Apply();
        Scroller.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        Closed += (_, _) => bitmap.Dispose();
    }

    // Fitted to the window, small pictures enlarged too (that is what the window is for).
    private double FitZoom()
    {
        if (_bitmap is null || _bitmap.Size.Width <= 0 || _bitmap.Size.Height <= 0)
        {
            return 1;
        }

        var width = Math.Max(1, Scroller.Bounds.Width - 24);
        var height = Math.Max(1, Scroller.Bounds.Height - 24);
        return Math.Min(Steps[^1], Math.Min(width / _bitmap.Size.Width, height / _bitmap.Size.Height));
    }

    private void Apply()
    {
        if (_bitmap is null)
        {
            return;
        }

        var zoom = _zoom ?? FitZoom();
        Picture.Width = _bitmap.Size.Width * zoom;
        Picture.Height = _bitmap.Size.Height * zoom;
        ZoomText.Text = $"{Math.Round(zoom * 100)} %";
    }

    private void Step(int direction)
    {
        var current = _zoom ?? FitZoom();
        _zoom = direction > 0
            ? Steps.FirstOrDefault(s => s > current + 0.001, Steps[^1])
            : Steps.LastOrDefault(s => s < current - 0.001, Steps[0]);
        Apply();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Delta.Y != 0)
        {
            e.Handled = true;
            Step(e.Delta.Y > 0 ? 1 : -1);
        }
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Step(1);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => Step(-1);

    private void OnFit(object? sender, RoutedEventArgs e)
    {
        _zoom = null;
        Apply();
    }

    private void OnActualSize(object? sender, RoutedEventArgs e)
    {
        _zoom = 1;
        Apply();
    }
}
