using Avalonia.Controls;
using Avalonia.Input;
using Neruna.Desktop.Infrastructure;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class QuickActionsView : UserControl
{
    public QuickActionsView()
    {
        InitializeComponent();
    }

    // "Tastenkürzel": the combination pressed in the field is taken; Backspace or Delete alone clears it.
    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: QuickActionEditor editor } || e.Key == Key.Tab)
        {
            return;
        }

        e.Handled = true;
        if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            editor.Shortcut = string.Empty;
        }
        else if (Shortcuts.Of(e) is { } shortcut)
        {
            editor.Shortcut = shortcut;
        }
    }
}
