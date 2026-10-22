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
internal sealed partial class AutoReplyViewModel(MailController mail, Neruna.Core.ISettingsStore settings, Account account, ServiceConnection connection) : ViewModelBase
{
    /// <summary>Closed; true when saved.</summary>
    public event EventHandler<bool>? Finished;

    /// <summary>What was stored on the server (set before <see cref="Finished"/> with true).</summary>
    public AutoReply? Saved { get; private set; }

    public AutoReplyFeatures Features { get; private set; }

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

    // ---- Series ("jeden Freitag"): defined in a template, or here -------------------------------------------------

    /// <summary>The server can send on certain weekdays: by itself (Sieve) or with Neruna setting the next day.</summary>
    public bool CanSeries => Features.HasFlag(AutoReplyFeatures.Weekly) || Features.HasFlag(AutoReplyFeatures.Schedule);

    /// <summary>Neruna sets the next day of the series on the server (Exchange, Microsoft 365).</summary>
    public bool IsSeriesRenewed => !Features.HasFlag(AutoReplyFeatures.Weekly);

    /// <summary>"Jede Woche an bestimmten Tagen" instead of a period.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPeriod))]
    public partial bool IsSeries { get; set; }

    public bool ShowsPeriod => CanSchedule && !IsSeries;

    public IReadOnlyList<WeekdayChoice> Weekdays { get; } = WeekdayChoice.Week();

    private IReadOnlyList<DayOfWeek> ChosenDays => [.. Weekdays.Where(d => d.IsChecked).Select(d => d.Day)];

    private void ShowDays(IReadOnlyCollection<DayOfWeek>? days)
    {
        IsSeries = days is { Count: > 0 };
        foreach (var choice in Weekdays)
        {
            choice.IsChecked = days?.Contains(choice.Day) == true;
        }
    }

    // ---- Named texts ("Ferien", "Bei Kunden"): for all accounts --------------------------------------------------

    public System.Collections.ObjectModel.ObservableCollection<AutoReplyTemplate> Templates { get; } = [];

    /// <summary>Choosing one puts its text (and subject) into the fields.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTemplateCommand))]
    public partial AutoReplyTemplate? SelectedTemplate { get; set; }

    /// <summary>Name for "Als Vorlage speichern" (an existing name is replaced).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTemplateCommand))]
    public partial string TemplateName { get; set; } = string.Empty;

    private bool _choosing;

    partial void OnSelectedTemplateChanged(AutoReplyTemplate? value)
    {
        if (_choosing || value is null)
        {
            return;
        }

        Message = value.Text;
        Subject = value.Subject ?? string.Empty;
        TemplateName = value.Name;
        ShowDays(CanSeries ? value.Weekdays : null);
    }

    private async Task LoadTemplatesAsync(string? select)
    {
        _choosing = true;
        try
        {
            Templates.Clear();
            foreach (var template in await AutoReplyText.LoadTemplatesAsync(settings))
            {
                Templates.Add(template);
            }

            SelectedTemplate = Templates.FirstOrDefault(t => select is not null ? t.Name == select : t.Text.Trim() == Message.Trim());
            TemplateName = SelectedTemplate?.Name ?? TemplateName;
        }
        finally
        {
            _choosing = false;
        }
    }

    private bool CanSaveTemplate() => TemplateName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSaveTemplate))]
    private async Task SaveTemplateAsync()
    {
        if (Message.Trim().Length == 0)
        {
            Error = T("Bitte einen Text für die automatische Antwort eingeben.");
            return;
        }

        if (IsSeries && ChosenDays.Count == 0)
        {
            Error = T("Bitte mindestens einen Tag für die Serie wählen.");
            return;
        }

        var name = TemplateName.Trim();
        await AutoReplyText.SaveTemplateAsync(settings, new AutoReplyTemplate(name, Message.Trim(), CanSetSubject && Subject.Trim().Length > 0 ? Subject.Trim() : null,
            IsSeries ? ChosenDays : null));
        await LoadTemplatesAsync(name);
        Error = null;
    }

    private bool CanDeleteTemplate() => SelectedTemplate is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteTemplate))]
    private async Task DeleteTemplateAsync()
    {
        await AutoReplyText.DeleteTemplateAsync(settings, SelectedTemplate!.Name);
        TemplateName = string.Empty;
        await LoadTemplatesAsync(null);
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var (reply, features) = await Task.Run(() => mail.GetAutoReplyAsync(connection));
            Features = features;
            OnPropertyChanged(nameof(CanSeries));
            OnPropertyChanged(nameof(IsSeriesRenewed));
            CanSchedule = features.HasFlag(AutoReplyFeatures.Schedule);
            CanSetSubject = features.HasFlag(AutoReplyFeatures.Subject);
            IsEnabled = reply.IsEnabled;
            var template = await AutoReplyText.TemplateForAsync(settings, connection.Id, reply);
            Message = template.Length > 0 ? template : AutoReplyText.Default(account.DisplayName);
            Subject = reply.Subject ?? string.Empty;
            IsScheduled = reply.IsScheduled && CanSchedule && !reply.IsRecurring;
            ShowDays(reply.Weekdays);
            await LoadTemplatesAsync(null);
            if (reply.IsScheduled && !reply.IsRecurring)
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
        if (IsSeries && ChosenDays.Count == 0)
        {
            Error = T("Bitte mindestens einen Tag für die Serie wählen.");
            return;
        }

        if (!IsSeries && IsScheduled && CanSchedule)
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

        AutoReply reply;
        try
        {
            reply = new AutoReply(IsEnabled, AutoReplyText.ForServer(Message, IsEnabled, start, end), start, end, CanSetSubject && Subject.Trim().Length > 0 ? Subject.Trim() : null,
                IsSeries ? ChosenDays : null);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message; // {{start}}/{{end}} without a period
            return;
        }

        IsBusy = true;
        try
        {
            await Task.Run(() => mail.SetAutoReplyAsync(account, connection, reply));
            await AutoReplyText.RememberAsync(settings, connection.Id, Message.Trim());
            Saved = reply;
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
}

/// <summary>A day of the week in a series ("Mo", "Di" …), as a toggle.</summary>
internal sealed partial class WeekdayChoice(DayOfWeek day) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Label { get; } = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day);

    public string Name { get; } = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day);

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public static List<WeekdayChoice> Week() => [.. AutoReplySeries.Week.Select(d => new WeekdayChoice(d))];
}
