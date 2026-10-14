using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Cloud;
using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Einstellungen → Datensicherung: the personal settings into a file protected by a password, and back – to move to a
/// new computer without Neruna Cloud. Both ask for the computer login first.
/// </summary>
internal sealed partial class FileBackupViewModel(SettingsBackupService backup, IFileService files) : ViewModelBase
{
    /// <summary>Settings were restored: everything reloads and syncs (as after a restore from the cloud).</summary>
    public event EventHandler? Restored;

    public LoginGate Login { get; } = new();

    public int MinLength { get; } = SettingsBackupService.MinFilePasswordLength;

    // ── In Datei sichern ──

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PasswordRepeat { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ExportMessage { get; set; }

    [ObservableProperty]
    public partial bool ExportFailed { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(OpenFileCommand), nameof(RestoreCommand))]
    public partial bool IsBusy { get; set; }

    private bool Idle() => !IsBusy;

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task ExportAsync()
    {
        ExportFailed = true;
        if (Password.Length < MinLength)
        {
            ExportMessage = F("Das Passwort muss mindestens {0} Zeichen lang sein.", MinLength);
            return;
        }

        if (Password != PasswordRepeat)
        {
            ExportMessage = T("Die beiden Passwörter stimmen nicht überein.");
            return;
        }

        ExportMessage = null;
        if (!await Login.VerifyAsync(T("Zum Sichern der Einstellungen in eine Datei bitte mit dem Login dieses Computers bestätigen.")))
        {
            return;
        }

        var name = $"Neruna-{DateTime.Now:yyyy-MM-dd}{SettingsBackupService.FileExtension}";
        await using var target = await files.SaveFileAsync(T("Sicherung speichern"), name);
        if (target is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await backup.ExportToFileAsync(target, Password);
            Password = PasswordRepeat = string.Empty;
            ExportFailed = false;
            ExportMessage = T("Gesichert. Bewahren Sie Datei und Passwort getrennt auf – ohne das Passwort lässt sich die Sicherung nicht öffnen, auch nicht von Neruna.");
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException)
        {
            ExportMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Aus Datei wiederherstellen ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    public partial string? FilePath { get; set; }

    public bool HasFile => FilePath is not null;

    [ObservableProperty]
    public partial string FilePassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    public partial RestorePlan? Plan { get; set; }

    public bool HasPlan => Plan is not null;

    public ObservableCollection<BackupChangeRow> Changes { get; } = [];

    [ObservableProperty]
    public partial string? PlanText { get; set; }

    [ObservableProperty]
    public partial string? RestoreMessage { get; set; }

    [ObservableProperty]
    public partial bool RestoreFailed { get; set; }

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task ChooseFileAsync()
    {
        RestoreMessage = null;
        Plan = null;
        Changes.Clear();
        if ((await files.PickFilesAsync(T("Sicherungsdatei wählen"))) is [var path, ..])
        {
            FilePath = path;
            FilePassword = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task OpenFileAsync()
    {
        if (FilePath is not { } path)
        {
            return;
        }

        IsBusy = true;
        RestoreFailed = true;
        try
        {
            await using var source = File.OpenRead(path);
            var plan = await backup.PrepareRestoreFromFileAsync(source, FilePassword);
            Changes.Clear();
            foreach (var change in plan.Changes.OrderBy(c => c.Area, StringComparer.CurrentCulture))
            {
                Changes.Add(new BackupChangeRow(change.Area, change.Name, change.Kind switch
                {
                    BackupChangeKind.Added => T("kommt dazu"),
                    BackupChangeKind.Removed => T("wird entfernt"),
                    _ => T("wird ersetzt"),
                }));
            }

            PlanText = F("Sicherung vom {0}{1}. Beim Wiederherstellen werden die persönlichen Einstellungen dieses Computers durch die der Sicherung ersetzt:",
                plan.Backup.CreatedAt.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture),
                plan.Backup.DeviceName is { Length: > 0 } device ? F(" ({0})", device) : string.Empty);
            Plan = plan;
            RestoreMessage = plan.Changes.Count == 0 ? T("Die Sicherung entspricht diesem Computer – nichts zu tun.") : null;
            RestoreFailed = false;
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException)
        {
            RestoreMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task RestoreAsync()
    {
        if (Plan is not { } plan || !await Login.VerifyAsync(T("Zum Wiederherstellen der Einstellungen bitte mit dem Login dieses Computers bestätigen.")))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await backup.RestoreAsync(plan);
            RestoreFailed = false;
            RestoreMessage = T("Wiederhergestellt. Die Konten werden jetzt synchronisiert.");
            CancelRestore();
            Restored?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is BackupException or IOException or InvalidOperationException)
        {
            RestoreFailed = true;
            RestoreMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelRestore()
    {
        Plan = null;
        FilePath = null;
        FilePassword = string.Empty;
        Changes.Clear();
    }
}
