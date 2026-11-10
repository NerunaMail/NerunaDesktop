using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Termine importieren": the events of an .ics file (a timetable, a school plan) copied into an own calendar – unlike
/// a subscription, no calendar of its own and changeable like any event.
/// </summary>
internal sealed partial class IcsImportViewModel : ViewModelBase
{
    private readonly CalendarController _calendar;
    private readonly Infrastructure.IFileService _files;
    private IReadOnlyList<ImportedEvent> _events = [];

    // Some were saved before others failed: the calendar shows them after closing.
    private bool _imported;

    public IcsImportViewModel(CalendarController calendar, Infrastructure.IFileService files, IReadOnlyList<CalendarInfo> writable, CalendarInfo? preferred)
    {
        _calendar = calendar;
        _files = files;
        Calendars = writable;
        SelectedCalendar = (preferred is null ? null : writable.FirstOrDefault(c => CalendarController.CalendarKey(c) == CalendarController.CalendarKey(preferred)))
                           ?? writable.FirstOrDefault(c => c.IsDefault) ?? writable.FirstOrDefault();
    }

    /// <summary>Closed: true when something was imported.</summary>
    public event EventHandler<bool>? Finished;

    /// <summary>"12 Termine importiert" for the status bar.</summary>
    public string? ResultMessage { get; private set; }

    public IReadOnlyList<CalendarInfo> Calendars { get; }

    public bool NoCalendar => Calendars.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial CalendarInfo? SelectedCalendar { get; set; }

    [ObservableProperty]
    public partial string? FileName { get; set; }

    /// <summary>"24 Termine gefunden: Mathematik, Deutsch, …".</summary>
    [ObservableProperty]
    public partial string? Found { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickFileCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Progress { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task PickFileAsync()
    {
        if (await _files.PickFileAsync(T("Kalenderdatei wählen"), T("iCalendar-Dateien"), ["*.ics", "*.ical", "*.ifb"]) is { } path)
        {
            await LoadAsync(path);
        }
    }

    private bool CanPick() => !IsBusy;

    /// <summary>Reads the file and says what is in it.</summary>
    public async Task LoadAsync(string path)
    {
        Error = null;
        Found = null;
        _events = [];
        FileName = Path.GetFileName(path);
        try
        {
            if (new FileInfo(path).Length > CalendarImport.MaxFileSize)
            {
                Error = T("Die Datei ist zu gross für einen Import.");
                return;
            }

            var text = await File.ReadAllTextAsync(path);
            _events = await Task.Run(() => CalendarImport.Read(text));
            var names = _events.Select(e => e.Summary).Where(s => s.Length > 0).Distinct(StringComparer.CurrentCulture).Take(4).ToList();
            Found = F("{0} Termin(e) gefunden", _events.Count) + (names.Count > 0 ? ": " + string.Join(", ", names) + (_events.Count > names.Count ? ", …" : string.Empty) : string.Empty);
        }
        catch (FormatException)
        {
            Error = T("Die Datei enthält keine Termine im iCalendar-Format (.ics).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = T("Die Datei konnte nicht gelesen werden: ") + ex.Message;
        }
        finally
        {
            ImportCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (SelectedCalendar is not { } target)
        {
            return;
        }

        Error = null;
        IsBusy = true;
        var total = _events.Count;
        try
        {
            var progress = new Progress<int>(done => Progress = F("{0} von {1} …", done, total));
            var result = await Task.Run(() => _calendar.ImportAsync(target, _events, progress));
            var message = result.Updated > 0
                ? F("{0} Termin(e) importiert, {1} aktualisiert.", result.Added, result.Updated)
                : F("{0} Termin(e) importiert.", result.Added);
            if (result.Failed > 0)
            {
                _imported = result.Added + result.Updated > 0;
                Error = message + " " + F("{0} konnten nicht gespeichert werden: {1}", result.Failed, result.FirstError ?? string.Empty);
                Progress = null;
                return;
            }

            ResultMessage = message;
            Finished?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = T("Import fehlgeschlagen: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanImport() => !IsBusy && SelectedCalendar is not null && _events.Count > 0;

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, _imported);
}
