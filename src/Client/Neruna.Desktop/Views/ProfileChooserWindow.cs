using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Neruna.Storage;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

/// <summary>
/// "Bei jedem Start Profil abfragen": which profile this start opens – before its data, so built in code without the
/// app's settings (theme, layout) which belong to a profile.
/// </summary>
internal sealed class ProfileChooserWindow : Window
{
    private readonly TaskCompletionSource<Profile?> _chosen = new();

    internal ProfileChooserWindow(ProfileList profiles)
    {
        Title = T("Neruna – Profil wählen");
        try
        {
            Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Neruna.Desktop/Assets/app.ico")));
        }
        catch (FileNotFoundException)
        {
            // Without the icon then; the start must not fail over it.
        }

        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var buttons = new StackPanel { Spacing = 8 };
        foreach (var profile in profiles.All)
        {
            var isDefault = profile.Id == profiles.DefaultId;
            var button = new Button
            {
                Content = new TextBlock { Text = profile.Name, FontSize = 15, FontWeight = isDefault ? FontWeight.SemiBold : FontWeight.Normal },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 10),
                IsDefault = isDefault,
            };
            if (isDefault)
            {
                button.Classes.Add("accent");
            }

            button.Click += (_, _) => Choose(profile);
            buttons.Children.Add(button);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = T("Welches Profil öffnen?"), FontSize = 18, FontWeight = FontWeight.SemiBold },
                buttons,
                new TextBlock
                {
                    Text = T("Abfrage ausschalten: Einstellungen → Profile."), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        Closed += (_, _) => _chosen.TrySetResult(null);
    }

    /// <summary>The chosen profile; null when the window was closed without a choice.</summary>
    public static Task<Profile?> AskAsync(ProfileList profiles)
    {
        var window = new ProfileChooserWindow(profiles);
        window.Show();
        window.Activate();
        return window._chosen.Task;
    }

    private void Choose(Profile profile)
    {
        _chosen.TrySetResult(profile);
        Close();
    }
}
