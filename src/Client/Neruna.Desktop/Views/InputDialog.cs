using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

/// <summary>A small modal question for a name ("Neuer Ordner"), checked while typing.</summary>
internal static class InputDialog
{
    /// <param name="check">What is wrong with the text (shown below it, OK stays off), or null when it can be used.</param>
    /// <returns>The text, or null when cancelled.</returns>
    public static async Task<string?> ShowInFrontAsync(string title, string label, string text, string okText, Func<string, string?> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if ((lifetime?.Windows.FirstOrDefault(w => w.IsActive) ?? lifetime?.MainWindow) is not { } owner)
        {
            return null;
        }

        string? result = null;
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

        var box = new TextBox { Text = text };
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        problem.Bind(TextBlock.ForegroundProperty, problem.GetResourceObservable("ErrorBrush"));
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = T("Abbrechen"), MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };

        // An unchanged name (renaming) is no error, just nothing to do: OK stays off without a message.
        void Check()
        {
            var value = box.Text ?? string.Empty;
            var message = value == text && text.Length > 0 ? null : check(value);
            problem.Text = message;
            problem.IsVisible = message is not null;
            ok.IsEnabled = message is null && value != text;
        }

        box.TextChanged += (_, _) => Check();
        ok.Click += (_, _) =>
        {
            result = box.Text;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
            }
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                box,
                problem,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } },
            },
        };
        Check();
        await dialog.ShowDialog(owner);
        return result;
    }
}
