using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Diagnostics;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Neruna wurde unerwartet beendet – Bericht senden?": what would be sent is shown in full under "Details anzeigen".
/// </summary>
internal sealed partial class CrashReportViewModel(IReadOnlyList<PendingCrash> crashes, CrashReportService service) : ViewModelBase
{
    public event EventHandler? Finished;

    public string Title => crashes.Any(c => c.Record.Fatal) ? T("Neruna wurde unerwartet beendet") : T("Es ist ein Fehler aufgetreten");

    public string Intro =>
        (crashes.Any(c => c.Record.Fatal) ? string.Empty : T("Neruna läuft weiter. ")) +
        T("Möchten Sie einen Fehlerbericht an Neruna senden? Er hilft, den Fehler zu beheben. Er enthält die Version, das Betriebssystem, die Fehlermeldung und die letzten Zeilen des Protokolls – E-Mail-Adressen, Namen, Server und Pfade werden vorher entfernt. Keine Nachrichten, Termine oder Kontakte.");

    public string Count => crashes.Count == 1 ? T("1 Bericht") : F("{0} Berichte", crashes.Count);

    /// <summary>Exactly what is sent.</summary>
    public string Details
    {
        get
        {
            var text = new StringBuilder();
            foreach (var crash in crashes)
            {
                var report = crash.Report;
                text.AppendLine(Neruna.Core.Localization.Texts.Culture, $"{report.OccurredAt.LocalDateTime:g} · Neruna {report.AppVersion} · {report.Os} · {report.Runtime} · {report.Language}")
                    .AppendLine(report.Fatal ? T("Absturz") : T("Abgefangen (Neruna lief weiter)"))
                    .AppendLine()
                    .AppendLine(report.Stack);
                if (report.Log is { Length: > 0 } log)
                {
                    text.AppendLine().AppendLine(T("Letzte Protokollzeilen:")).AppendLine(log);
                }

                text.AppendLine().AppendLine(new string('─', 60)).AppendLine();
            }

            return text.ToString().TrimEnd('─', '\n', '\r', ' ');
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsLabel))]
    public partial bool ShowDetails { get; set; }

    public string DetailsLabel => (ShowDetails ? T("Details ausblenden") : T("Details anzeigen")) + $" ({Count})";

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

    [ObservableProperty]
    public partial bool AlwaysSend { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(DontSendCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SendAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            if (AlwaysSend)
            {
                await service.SetModeAsync(CrashReportMode.Always);
            }

            var sent = await service.SendAsync(crashes);
            if (sent < crashes.Count && service.Store.Pending().Any(p => crashes.Any(c => c.File == p.File)))
            {
                Error = T("Der Bericht konnte nicht gesendet werden (keine Verbindung?). Er wird beim nächsten Start erneut angeboten.");
                IsBusy = false;
                return;
            }

            Finished?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void DontSend()
    {
        service.Discard(crashes);
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private bool IsIdle() => !IsBusy;
}
