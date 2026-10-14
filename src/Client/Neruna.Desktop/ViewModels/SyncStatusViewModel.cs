using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Accounts;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>One connection in the status window: account, service, how its synchronisation is going.</summary>
internal sealed record SyncRow(string Account, string Service, string State, string? Detail, bool IsRunning, bool IsFailed)
{
    public bool HasDetail => Detail is not null;
}

/// <summary>
/// "Synchronisation": per account and service what runs (and since when), how the last run ended and why one failed –
/// opened from the status bar. Updated every second while a run is going.
/// </summary>
internal sealed partial class SyncStatusViewModel : ViewModelBase
{
    private readonly SyncActivity _activity;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public SyncStatusViewModel(SyncActivity activity, IAsyncRelayCommand syncNow)
    {
        _activity = activity;
        SyncNowCommand = syncNow;
        _activity.Changed += OnChanged;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public event EventHandler? Finished;

    public ObservableCollection<SyncRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public IAsyncRelayCommand SyncNowCommand { get; }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        Rows.Clear();
        foreach (var entry in _activity.Entries)
        {
            Rows.Add(RowOf(entry, now));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private static SyncRow RowOf(SyncEntry entry, DateTimeOffset now)
    {
        var service = entry.Kind switch
        {
            ServiceKind.Mail => T("E-Mail"),
            ServiceKind.Calendar => T("Kalender"),
            _ => T("Kontakte"),
        };
        var ended = entry.StartedAt + (entry.Duration ?? TimeSpan.Zero);
        var lastSuccess = entry.LastSuccess is { } success ? F("Zuletzt erfolgreich: {0}", success.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)) : null;
        return entry.State switch
        {
            SyncState.Running => new SyncRow(entry.Account, service, F("Läuft seit {0}", Elapsed(now - entry.StartedAt)), lastSuccess, true, false),
            SyncState.Failed => new SyncRow(entry.Account, service, F("Fehlgeschlagen um {0:t}", ended.LocalDateTime), entry.Error + (lastSuccess is null ? string.Empty : " · " + lastSuccess), false, true),
            SyncState.Skipped => new SyncRow(entry.Account, service, T("In dieser Version nicht unterstützt"), null, false, false),
            _ => new SyncRow(entry.Account, service, F("Synchronisiert um {0:t} ({1})", ended.LocalDateTime, Elapsed(entry.Duration ?? TimeSpan.Zero)), null, false, false),
        };
    }

    private static string Elapsed(TimeSpan span) =>
        span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}:{span.Seconds:00} min"
            : F("{0} s", Math.Max(0, (int)Math.Round(span.TotalSeconds)));

    [RelayCommand]
    private void Close()
    {
        _timer.Stop();
        _activity.Changed -= OnChanged;
        Finished?.Invoke(this, EventArgs.Empty);
    }
}
