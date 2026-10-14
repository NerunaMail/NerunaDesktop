using Avalonia.Controls;
using Avalonia.Input;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class ReadingPaneView : UserControl
{
    public ReadingPaneView()
    {
        InitializeComponent();
    }

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
