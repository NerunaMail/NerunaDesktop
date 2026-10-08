using Avalonia.Controls;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class ComposeWindow : Window
{
    public ComposeWindow()
    {
        InitializeComponent();
    }

    // Closing with ✕ keeps what was typed: the text is taken first, then stored in "Entwürfe".
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || DataContext is not ComposeViewModel { IsFinished: false } compose)
        {
            return;
        }

        e.Cancel = true;
        await compose.SaveOnLeaveAsync();
        Close();
    }
}
