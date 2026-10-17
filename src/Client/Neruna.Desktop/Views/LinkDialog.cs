using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Neruna.Core.Privacy;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

/// <summary>
/// Before a link from a mail opens (Einstellungen → Datenschutz): what the mail shows, where the link really goes,
/// and what the check noticed. For a suspicious link, "Abbrechen" is the default.
/// </summary>
internal static class LinkDialog
{
    private static readonly IBrush HighBrush = new SolidColorBrush(Color.Parse("#C50F1F"));
    private static readonly IBrush MediumBrush = new SolidColorBrush(Color.Parse("#BC4B09"));

    /// <returns>True: open it (<see cref="LinkCheck.Opens"/>).</returns>
    public static async Task<bool> ShowAsync(Window owner, LinkCheck check)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(check);
        var open = false;
        var dialog = new Window
        {
            Title = check.IsSuspicious ? T("Verdächtiger Link") : T("Link öffnen"),
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = owner.Icon,
        };
        dialog.Bind(Window.BackgroundProperty, dialog.GetResourceObservable("PaneBackgroundBrush"));

        var content = new StackPanel { Margin = new Thickness(24, 20), Spacing = 10 };
        if (!string.IsNullOrWhiteSpace(check.Text))
        {
            content.Children.Add(Label(T("Angezeigter Text")));
            content.Children.Add(new SelectableTextBlock { Text = check.Text, TextWrapping = TextWrapping.Wrap });
        }

        if (check.Host is { } host)
        {
            content.Children.Add(Label(T("Führt zu")));
            content.Children.Add(new SelectableTextBlock { Text = host, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        }

        content.Children.Add(new SelectableTextBlock { Text = check.Target is null ? check.Href : check.Opens, FontSize = 12, Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap });

        foreach (var finding in check.Findings.OrderByDescending(f => f.Severity))
        {
            var brush = finding.Severity switch { Severity.High => HighBrush, Severity.Medium => MediumBrush, _ => null };
            var title = new TextBlock { Text = (finding.Severity >= Severity.Medium ? "⚠ " : "• ") + finding.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            if (brush is not null)
            {
                title.Foreground = brush;
            }

            var item = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0), Children = { title } };
            if (finding.Detail is { } detail)
            {
                item.Children.Add(new TextBlock { Text = detail, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            }

            content.Children.Add(item);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var copy = new Button { Content = T("Link kopieren"), MinWidth = 90 };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(dialog)?.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, check.Target is null ? check.Href : check.Opens);
            }
        };
        var openButton = new Button { Content = check.IsSuspicious ? T("Trotzdem öffnen") : T("Öffnen"), MinWidth = 90, IsDefault = !check.IsSuspicious, IsEnabled = check.Target is not null };
        if (!check.IsSuspicious)
        {
            openButton.Classes.Add("accent");
        }

        openButton.Click += (_, _) =>
        {
            open = true;
            dialog.Close();
        };
        var cancel = new Button { Content = T("Abbrechen"), MinWidth = 90, IsDefault = check.IsSuspicious };
        if (check.IsSuspicious)
        {
            cancel.Classes.Add("accent");
        }

        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(openButton);
        buttons.Children.Add(cancel);
        content.Children.Add(buttons);

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
            }
        };
        dialog.Content = content;
        await dialog.ShowDialog(owner);
        return open;
    }

    /// <summary>A link to a program or script (from a safe sender): opened only after asking once more.</summary>
    /// <returns>True: open it.</returns>
    public static async Task<bool> ShowProgramAsync(Window owner, string path)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var open = false;
        var dialog = new Window
        {
            Title = T("Programm starten?"),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = owner.Icon,
        };
        dialog.Bind(Window.BackgroundProperty, dialog.GetResourceObservable("PaneBackgroundBrush"));
        var openButton = new Button { Content = T("Trotzdem öffnen"), MinWidth = 90 };
        openButton.Click += (_, _) =>
        {
            open = true;
            dialog.Close();
        };
        var cancel = new Button { Content = T("Abbrechen"), MinWidth = 90, IsDefault = true, IsCancel = true, Classes = { "accent" } };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "⚠ " + T("Der Link startet ein Programm oder Skript."), FontWeight = FontWeight.SemiBold, Foreground = HighBrush, TextWrapping = TextWrapping.Wrap },
                new SelectableTextBlock { Text = path, FontSize = 12, Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = T("Öffnen Sie es nur, wenn Sie es erwartet haben – auch ein sicherer Absender kann gehackt sein."), TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { openButton, cancel } },
            },
        };
        await dialog.ShowDialog(owner);
        return open;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Classes = { "subtle" }, Margin = new Thickness(0, 4, 0, 0) };
}
