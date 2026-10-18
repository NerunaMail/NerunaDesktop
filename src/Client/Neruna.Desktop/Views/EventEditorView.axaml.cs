using Avalonia.Controls;
using Avalonia.Interactivity;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class EventEditorView : UserControl
{
    public EventEditorView()
    {
        InitializeComponent();
    }

    private async void OnJoinMeeting(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EventEditorViewModel { MeetingUrl: { } url } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && TopLevel.GetTopLevel(this) is { } top)
        {
            await top.Launcher.LaunchUriAsync(uri);
        }
    }

    private async void OnCopyMeetingLink(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EventEditorViewModel { MeetingUrl: { } url } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, url);
        }
    }
}
