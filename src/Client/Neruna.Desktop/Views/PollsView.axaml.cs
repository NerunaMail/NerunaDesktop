using Avalonia;
using Avalonia.Controls;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class PollsView : UserControl
{
    public PollsView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is PollsViewModel polls)
        {
            polls.CopyRequested -= OnCopy;
            polls.CopyRequested += OnCopy;
        }
    }

    private async void OnCopy(object? sender, string link)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, link);
        }
    }
}
