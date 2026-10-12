using Avalonia.Controls;
using Avalonia.Interactivity;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class AiOptionsView : UserControl
{
    public AiOptionsView()
    {
        InitializeComponent();
    }

    // "Kopieren": the sign-in address to the clipboard, for another browser.
    private async void OnCopySignInUrl(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AiOptionsViewModel { SignInUrl: { } url } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, url);
        }
    }
}
