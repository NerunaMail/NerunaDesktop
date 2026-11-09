using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Neruna.Desktop.Infrastructure;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class QuickActionsView : UserControl
{
    public QuickActionsView()
    {
        InitializeComponent();
        // Tunnelling, ahead of the TextBox: it takes Backspace and Delete itself (even read-only), so a handler on the
        // bubbling KeyDown never saw them and the shortcut could not be cleared.
        ShortcutBox.AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
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
