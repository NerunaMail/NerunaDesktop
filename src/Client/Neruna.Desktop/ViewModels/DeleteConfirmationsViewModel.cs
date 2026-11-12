using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core;
using Neruna.Desktop.Infrastructure;

namespace Neruna.Desktop.ViewModels;

/// <summary>Einstellungen → Daten, "Vor dem Löschen fragen": a ticked "Nicht mehr fragen" is undone here (and the other way round).</summary>
internal sealed partial class DeleteConfirmationsViewModel(ISettingsStore settings) : ViewModelBase
{
    private bool _loading;

    [ObservableProperty]
    public partial bool Contacts { get; set; } = true;

    [ObservableProperty]
    public partial bool Events { get; set; } = true;

    [ObservableProperty]
    public partial bool Tasks { get; set; } = true;

    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            Contacts = await settings.GetBoolAsync(DeleteConfirmation.KeyOf(DeleteKind.Contact), fallback: true);
            Events = await settings.GetBoolAsync(DeleteConfirmation.KeyOf(DeleteKind.Event), fallback: true);
            Tasks = await settings.GetBoolAsync(DeleteConfirmation.KeyOf(DeleteKind.Task), fallback: true);
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnContactsChanged(bool value) => Save(DeleteKind.Contact, value);

    partial void OnEventsChanged(bool value) => Save(DeleteKind.Event, value);

    partial void OnTasksChanged(bool value) => Save(DeleteKind.Task, value);

    private void Save(DeleteKind kind, bool ask)
    {
        if (!_loading)
        {
            _ = settings.SetBoolAsync(DeleteConfirmation.KeyOf(kind), ask);
        }
    }
}
