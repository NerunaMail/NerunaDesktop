using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

/// <summary>A small modal question with a few buttons (e.g. "Entwurf speichern?" on closing).</summary>
internal static class ChoiceDialog
{
    /// <summary>The dialog on screen, if any (the snapshot tool photographs it).</summary>
    public static Window? Open { get; private set; }

    /// <summary>Over the window in front (a compose or message window, or the main window).</summary>
    /// <returns>The chosen result; the last choice (cancel) also when there is no window.</returns>
    public static Task<T> ShowInFrontAsync<T>(string title, string message, params (string Text, T Result, bool IsDefault)[] choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        return (lifetime?.Windows.FirstOrDefault(w => w.IsActive) ?? lifetime?.MainWindow) is { } owner
            ? ShowAsync(owner, title, message, choices)
            : Task.FromResult(choices[^1].Result);
    }

    /// <param name="choices">Button text, result, and whether it is the highlighted default (Enter).</param>
    /// <returns>The chosen result; the last choice (cancel) if the dialog is closed otherwise.</returns>
    public static async Task<T> ShowAsync<T>(Window owner, string title, string message, params (string Text, T Result, bool IsDefault)[] choices)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(choices);
        var result = choices[^1].Result;
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = owner.Icon,
        };
        dialog.Bind(Window.BackgroundProperty, dialog.GetResourceObservable("PaneBackgroundBrush"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (text, value, isDefault) in choices)
        {
            var button = new Button { Content = text, IsDefault = isDefault, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (isDefault)
            {
                button.Classes.Add("accent");
            }

            button.Click += (_, _) =>
            {
                result = value;
                dialog.Close();
            };
            buttons.Children.Add(button);
        }

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
            }
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Spacing = 20,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, buttons },
        };

        Open = dialog;
        try
        {
            await dialog.ShowDialog(owner);
        }
        finally
        {
            Open = null;
        }

        return result;
    }
}
