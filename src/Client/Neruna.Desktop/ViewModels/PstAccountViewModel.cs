using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using Neruna.Desktop.Infrastructure;
using Neruna.Providers.Pst;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Outlook-Datendatei (.pst) einbinden": an old archive as an account of its own, read only. Also for changing its
/// name or pointing it to the file at a new place.
/// </summary>
internal sealed partial class PstAccountViewModel(AccountSetupService setup, IAccountStore accounts, MailController mail, IFileService files, Account? editing = null) : ViewModelBase
{
    public event EventHandler<bool>? Finished;

    public bool IsEditing => editing is not null;

    public string Heading => IsEditing ? T("Outlook-Datendatei bearbeiten") : T("Outlook-Datendatei einbinden");

    public string ConfirmText => IsEditing ? T("Speichern") : T("Einbinden");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Path { get; set; } = editing?.Connections.FirstOrDefault()?.Settings.GetValueOrDefault(PstMailProvider.PathSetting) ?? string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = editing?.DisplayName ?? string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The created or changed account (the shell shows its mail).</summary>
    public Account? Account { get; private set; }

    [RelayCommand]
    private async Task ChooseAsync()
    {
        if (await files.PickFileAsync(T("Outlook-Datendatei auswählen"), T("Outlook-Datendateien"), ["*.pst", "*.ost"]) is { } path)
        {
            Path = path;
            if (string.IsNullOrWhiteSpace(Name) || !IsEditing)
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(path);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var created = PstMailProviderFactory.AccountFor(Path.Trim(), Name);
            if (editing is not null)
            {
                // The same file: only the name changes (read/flagged stay). Another file: a new connection – the old one's
                // folders and messages go, the new file is read anew.
                var connection = editing.Connections[0];
                var path = Path.Trim();
                if (connection.Settings.GetValueOrDefault(PstMailProvider.PathSetting) != path)
                {
                    connection = created.Connections[0];
                    await using var provider = new PstMailProviderFactory().Create(connection);
                    await provider.TestConnectionAsync();
                }

                var changed = editing with { DisplayName = created.DisplayName, Connections = [connection] };
                await accounts.SaveAccountAsync(changed);
                Account = changed;
            }
            else
            {
                await setup.CreateAsync(created, password: null);

                // The file is on this computer: nothing to keep a copy of.
                await mail.SetOfflinePeriodAsync(created.Id, OfflinePeriod.OpenedOnly);
                Account = created;
            }

            Finished?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is AccountSetupException or PstException or IOException or UnauthorizedAccessException)
        {
            Error = T("Die Datei lässt sich nicht als Outlook-Datendatei lesen: ") + (ex.InnerException as PstException ?? ex).Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConfirm() => !IsBusy && Path.Trim().Length > 0;

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);
}
