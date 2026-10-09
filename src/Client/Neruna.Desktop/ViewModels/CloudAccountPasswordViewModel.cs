using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Accounts;
using Neruna.Core.Cloud;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// An account the organisation set up without a password: the user types it once, it is tested and the account is set
/// up. "Später" asks again on the next start.
/// </summary>
internal sealed partial class CloudAccountPasswordViewModel(PendingCloudAccount pending, CloudAccountSync sync) : ViewModelBase
{
    public event EventHandler<bool>? Finished;

    public string Intro =>
        F("Ihre Organisation hat das Konto «{0} <{1}>» für Sie eingerichtet. Geben Sie das Passwort ein, um die Einrichtung abzuschliessen – es wird nur auf diesem Gerät gespeichert.", pending.DisplayName, pending.Email);

    public string Email => pending.Email;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task CompleteAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            await sync.CompleteAsync(pending, Password);
            Finished?.Invoke(this, true);
        }
        catch (AccountSetupException ex)
        {
            Error = T("Anmeldung fehlgeschlagen – ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanComplete() => !IsBusy && Password.Length > 0;

    [RelayCommand]
    private void Later() => Finished?.Invoke(this, false);
}
