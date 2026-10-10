using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Abwesenheitsnotiz" of one mail account: on/off, the text, optionally a period and a subject – as far as the server
/// supports them. Stored on the server, which answers while Neruna is closed.
/// </summary>
internal sealed partial class AutoReplyViewModel(MailController mail, Account account, ServiceConnection connection) : ViewModelBase
{
    /// <summary>Closed; true when saved.</summary>
    public event EventHandler<bool>? Finished;

    public string Title => F("Abwesenheitsnotiz · {0}", account.Title);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; } = true;

    /// <summary>Why this account cannot have one (no ManageSieve, a missing permission …).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string? Unavailable { get; set; }

    public bool IsReady => !IsBusy && Unavailable is null;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanSchedule { get; set; }

    [ObservableProperty]
    public partial bool CanSetSubject { get; set; }

    /// <summary>"Nur in diesem Zeitraum".</summary>
    [ObservableProperty]
    public partial bool IsScheduled { get; set; }

    [ObservableProperty]
    public partial DateTime? FirstDay { get; set; } = DateTime.Today;

    /// <summary>The last day away (inclusive).</summary>
    [ObservableProperty]
    public partial DateTime? LastDay { get; set; } = DateTime.Today.AddDays(7);

    [ObservableProperty]
    public partial string Subject { get; set; } = string.Empty;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var (reply, features) = await Task.Run(() => mail.GetAutoReplyAsync(connection));
            CanSchedule = features.HasFlag(AutoReplyFeatures.Schedule);
            CanSetSubject = features.HasFlag(AutoReplyFeatures.Subject);
            IsEnabled = reply.IsEnabled;
            Message = reply.Message.Length > 0 ? reply.Message : DefaultMessage();
            Subject = reply.Subject ?? string.Empty;
            IsScheduled = reply.IsScheduled && CanSchedule;
            if (reply.IsScheduled)
            {
                FirstDay = reply.Start!.Value.LocalDateTime.Date;
                LastDay = reply.End!.Value.LocalDateTime.Date.AddDays(-1);
            }
        }
        catch (AutoReplyUnavailableException ex)
        {
            Unavailable = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Unavailable = F("Die Abwesenheitsnotiz konnte nicht gelesen werden: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => IsReady;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Error = null;
        if (IsEnabled && Message.Trim().Length == 0)
        {
            Error = T("Bitte einen Text für die automatische Antwort eingeben.");
            return;
        }

        DateTimeOffset? start = null, end = null;
        if (IsScheduled && CanSchedule)
        {
            if (FirstDay is not { } first || LastDay is not { } last || last.Date < first.Date)
            {
                Error = T("Bitte einen gültigen Zeitraum wählen (der letzte Tag nicht vor dem ersten).");
                return;
            }

            // The end is exclusive: the first day back.
            start = new DateTimeOffset(first.Date, TimeZoneInfo.Local.GetUtcOffset(first.Date));
            end = new DateTimeOffset(last.Date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(last.Date.AddDays(1)));
        }

        var reply = new AutoReply(IsEnabled, Message.Trim(), start, end, CanSetSubject && Subject.Trim().Length > 0 ? Subject.Trim() : null);
        IsBusy = true;
        try
        {
            await Task.Run(() => mail.SetAutoReplyAsync(account, connection, reply));
            Finished?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex is AutoReplyUnavailableException ? ex.Message : F("Die Abwesenheitsnotiz konnte nicht gespeichert werden: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);

    private string DefaultMessage() =>
        F("Guten Tag\n\nIch bin zurzeit nicht im Büro und lese meine E-Mails nicht regelmässig. Ihre Nachricht wird nach meiner Rückkehr bearbeitet.\n\nFreundliche Grüsse\n{0}", account.DisplayName);
}
