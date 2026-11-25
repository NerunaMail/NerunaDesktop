using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Controls.ApplicationLifetimes;
using Neruna.Core;
using Neruna.Core.Mail;
using Neruna.Core.Security;
using Neruna.Desktop.ViewModels;
using Neruna.Desktop.Views;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// Opens a link from a note, description or location (and the non-web links of a mail): web links in the browser,
/// mail links as a new mail in Neruna, phone numbers in the phone program; links into other programs and files only
/// after asking (see <see cref="LinkKinds"/>).
/// </summary>
internal static class LinkOpener
{
    /// <summary>Set at start; holds the link kinds opened without asking.</summary>
    public static ISettingsStore? Settings { get; set; }

    public static async Task OpenAsync(Control from, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(uri);
        if (TopLevel.GetTopLevel(from) is not { } top || LinkKinds.IsBlocked(uri))
        {
            return;
        }

        switch (LinkKinds.Of(uri))
        {
            case LinkKind.Web:
                await top.Launcher.LaunchUriAsync(uri);
                return;
            case LinkKind.Mail:
                var address = Uri.UnescapeDataString(uri.OriginalString[7..].Split('?')[0]);
                var main = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext as MainWindowViewModel;
                if (main is not null)
                {
                    await main.MailPage.ComposeToAsync(address);
                }

                return;
            case LinkKind.Phone:
                if (PhoneLink.Normalize(uri.OriginalString) is { } phone)
                {
                    await top.Launcher.LaunchUriAsync(new Uri(phone));
                }

                return;
        }

        if (top is not Window owner)
        {
            return;
        }

        if (uri.IsFile)
        {
            var path = uri.LocalPath;
            if (FileLinks.IsExecutable(path) ? !await LinkDialog.ShowProgramAsync(owner, path) : !(await LinkDialog.ShowOtherAsync(owner, uri, canRemember: false)).Open)
            {
                return;
            }

            _ = Directory.Exists(path)
                ? await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path))
                : File.Exists(path) && await top.Launcher.LaunchFileInfoAsync(new FileInfo(path));
            return;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var trusted = Settings is not null && (await Settings.GetSetAsync(SettingKeys.TrustedLinkSchemes)).Contains(scheme);
        if (!trusted)
        {
            var (open, remember) = await LinkDialog.ShowOtherAsync(owner, uri, LinkKinds.CanTrust(uri) && Settings is not null);
            if (!open)
            {
                return;
            }

            if (remember && Settings is not null)
            {
                await Settings.SetMembershipAsync(SettingKeys.TrustedLinkSchemes, scheme, member: true);
            }
        }

        await top.Launcher.LaunchUriAsync(uri);
    }
}
