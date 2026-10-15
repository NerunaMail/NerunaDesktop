using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Einstellungen → Daten → Lokale Daten: the database on this computer – its size, and making it smaller (SQLite reuses
/// freed space but does not give it back by itself).
/// </summary>
internal sealed partial class LocalDataViewModel(IStorageMaintenance? storage = null) : ViewModelBase
{
    private readonly IStorageMaintenance? _storage = storage;


    /// <summary>"Datenbank: 412 MB".</summary>
    [ObservableProperty]
    public partial string DatabaseSizeText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompactCommand))]
    public partial bool IsCompacting { get; set; }

    [ObservableProperty]
    public partial string? CompactResult { get; set; }

    public void ShowDatabaseSize()
    {
        if (_storage is not null)
        {
            DatabaseSizeText = F("Datenbank auf diesem Computer: {0}", Megabytes(_storage.DatabaseSize));
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanCompact))]
    private async Task CompactAsync()
    {
        if (_storage is not { } storage)
        {
            return;
        }

        IsCompacting = true;
        CompactResult = null;
        try
        {
            var before = storage.DatabaseSize;
            await Task.Run(() => storage.CompactAsync());
            var after = storage.DatabaseSize;
            CompactResult = F("Verkleinert von {0} auf {1}.", Megabytes(before), Megabytes(after));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException)
        {
            CompactResult = T("Verkleinern fehlgeschlagen: ") + ex.Message;
        }
        finally
        {
            IsCompacting = false;
            ShowDatabaseSize();
        }
    }

    private bool CanCompact() => !IsCompacting && _storage is not null;

    private static string Megabytes(long bytes) => F("{0:N1} MB", bytes / 1024.0 / 1024.0);
}
