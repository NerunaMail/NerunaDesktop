using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Neruna.Desktop.ViewModels;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

/// <summary>
/// The signers of a mail with their certificates (or PGP keys) in a window of its own: wide enough for the full names
/// and fingerprints, values selectable to copy, closed with «Schliessen» or Escape.
/// </summary>
internal static class SignerDetailsDialog
{
    /// <summary>The dialog on screen, if any (the snapshot tool photographs it).</summary>
    public static Window? Open { get; private set; }

    /// <summary>Over the window in front (a message window or the main window); nothing without a window.</summary>
    public static async Task ShowInFrontAsync(IReadOnlyList<SignerDetails> signers)
    {
        ArgumentNullException.ThrowIfNull(signers);
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if ((lifetime?.Windows.FirstOrDefault(w => w.IsActive) ?? lifetime?.MainWindow) is { } owner)
        {
            await ShowAsync(owner, signers);
        }
    }

    /// <summary>Over <paramref name="owner"/>.</summary>
    public static async Task ShowAsync(Window owner, IReadOnlyList<SignerDetails> signers)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(signers);
        var dialog = new Window
        {
            Title = T("Zertifikat"),
            Width = 760,
            MinWidth = 480,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 720,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = owner.Icon,
        };
        dialog.Bind(Window.BackgroundProperty, dialog.GetResourceObservable("PaneBackgroundBrush"));
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
            }
        };

        var list = new StackPanel { Spacing = 20 };
        foreach (var signer in signers)
        {
            var block = new StackPanel { Spacing = 6 };
            block.Children.Add(new SelectableTextBlock { Text = signer.Title, FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16, RowSpacing = 4 };
            for (var i = 0; i < signer.Rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = new TextBlock { Text = signer.Rows[i].Label, Classes = { "subtle" } };
                var value = new SelectableTextBlock { Text = signer.Rows[i].Value, TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
            }

            block.Children.Add(grid);
            list.Children.Add(block);
        }

        var close = new Button { Content = T("Schliessen"), IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        close.Classes.Add("accent");
        close.Click += (_, _) => dialog.Close();

        var content = new DockPanel { Margin = new Thickness(24, 20) };
        DockPanel.SetDock(close, Dock.Bottom);
        close.Margin = new Thickness(0, 20, 0, 0);
        content.Children.Add(close);
        content.Children.Add(new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        dialog.Content = content;

        Open = dialog;
        try
        {
            await dialog.ShowDialog(owner);
        }
        finally
        {
            Open = null;
        }
    }
}
