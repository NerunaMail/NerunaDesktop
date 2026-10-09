using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Neruna.Core.Auth;

namespace Neruna.Desktop.Infrastructure;

/// <summary>Opens sign-in pages (OAuth) in the user's default browser, where saved logins and passkeys work.</summary>
internal sealed class BrowserLauncher : IBrowserLauncher
{
    public async Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as TopLevel;
        if (window is null || !await window.Launcher.LaunchUriAsync(uri))
        {
            throw new OAuthException(Neruna.Core.Localization.Texts.T("Der Browser für die Anmeldung konnte nicht geöffnet werden."));
        }
    }
}
