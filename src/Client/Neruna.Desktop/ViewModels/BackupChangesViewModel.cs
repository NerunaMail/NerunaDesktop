using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Cloud;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>A line of "Seit der letzten Sicherung geändert".</summary>
internal sealed record BackupChangeRow(string Area, string Name, string What);

/// <summary>
/// Opened from "Einstellungen geändert – neue Sicherung erstellen?": what changed since the last backup, item by item,
/// so one can judge whether a backup is worth it.
/// </summary>
internal sealed partial class BackupChangesViewModel(IReadOnlyList<BackupChange>? changes, IRelayCommand backupNow, IAsyncRelayCommand later) : ViewModelBase
{
    public event EventHandler? Finished;

    /// <summary>
    /// Settings by what they are about ("Datenschutz", "Kalenderfarben"), not by their keys: several keys with the same
    /// label are one line, "neu"/"entfernt" only when all of them are.
    /// </summary>
    public IReadOnlyList<BackupChangeRow> Rows { get; } = changes is null ? [] :
        [.. changes.Select(c => c.Area == T("Einstellungen") ? c with { Name = SettingLabels.Of(c.Name) } : c)
            .GroupBy(c => (c.Area, c.Name))
            .Select(g => new BackupChange(g.Key.Area, g.Key.Name, g.Select(c => c.Kind).Distinct().Count() == 1 ? g.First().Kind : BackupChangeKind.Changed))
            .OrderBy(c => c.Area, StringComparer.CurrentCulture).ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .Select(c => new BackupChangeRow(c.Area, c.Name, c.Kind switch
            {
                BackupChangeKind.Added => T("neu"),
                BackupChangeKind.Removed => T("entfernt"),
                _ => T("geändert"),
            }))];

    public bool HasRows => Rows.Count > 0;

    /// <summary>The last backup is older than this list: the details come with the next backup.</summary>
    public bool IsUnknown => changes is null;

    public bool IsEmpty => changes is { Count: 0 };

    [RelayCommand]
    private void BackupNow()
    {
        Finished?.Invoke(this, EventArgs.Empty);
        backupNow.Execute(null);
    }

    [RelayCommand]
    private async Task LaterAsync()
    {
        Finished?.Invoke(this, EventArgs.Empty);
        await later.ExecuteAsync(null);
    }

    [RelayCommand]
    private void Close() => Finished?.Invoke(this, EventArgs.Empty);
}
