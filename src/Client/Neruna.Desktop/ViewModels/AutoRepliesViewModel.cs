using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// The out-of-office replies of all mail accounts that support one – behind the button at the top of the window:
/// a switch and an optional period per account, saved at once; "Weitere Optionen …" opens the full dialog (text).
/// </summary>
internal sealed partial class AutoRepliesViewModel(MailController mail, ISettingsStore settings, ILogger<AutoRepliesViewModel> logger) : ViewModelBase
{
    /// <summary>Which accounts answer automatically changed (folder tree and settings show it too).</summary>
    public event EventHandler? Changed;

    /// <summary>"Weitere Optionen …" of an account.</summary>
    public event EventHandler<AutoReplyAccountViewModel>? EditRequested;

    /// <summary>The accounts whose server can answer automatically (others do not appear).</summary>
    public ObservableCollection<AutoReplyAccountViewModel> Accounts { get; } = [];

    /// <summary>"Ferien", "Bei Kunden" …: chosen per account, managed in "Weitere Optionen".</summary>
    public ObservableCollection<AutoReplyTemplate> Templates { get; } = [];

    public bool HasTemplates => Templates.Count > 0;

    public async Task ReloadTemplatesAsync()
    {
        Templates.Clear();
        foreach (var template in await AutoReplyText.LoadTemplatesAsync(settings))
        {
            Templates.Add(template);
        }

        OnPropertyChanged(nameof(HasTemplates));
        foreach (var row in Accounts)
        {
            row.MatchTemplate();
        }
    }

    public bool IsAvailable => Accounts.Count > 0;

    public bool AnyActive => Accounts.Any(a => a.IsActive);

    /// <summary>"Abwesenheit" when none is on, "Abwesend" / "Abwesend · 2 Konten" otherwise.</summary>
    public string HeaderText => Accounts.Count(a => a.IsActive) switch
    {
        0 => T("Abwesenheit"),
        1 => T("Abwesend"),
        var n => F("Abwesend · {0} Konten", n),
    };

    /// <summary>For the tooltip: who answers automatically, and until when.</summary>
    public string Summary => AnyActive
        ? string.Join("\n", Accounts.Where(a => a.IsActive).Select(a => a.Title + " – " + a.Status))
        : T("Abwesenheitsnotiz: keine eingeschaltet");

    /// <summary>Mail connections whose reply is on now.</summary>
    public IReadOnlySet<Guid> ActiveConnections => Accounts.Where(a => a.IsActive).Select(a => a.Connection.Id).ToHashSet();

    /// <summary>
    /// Asks every account's server (at start, then hourly) – all at once, so a server without ManageSieve does not
    /// hold up the others. An account whose server cannot do it leaves the list; an unknown answer keeps the row.
    /// </summary>
    public async Task RefreshAsync(IReadOnlyList<(Account Account, ServiceConnection Connection)> mailAccounts)
    {
        ArgumentNullException.ThrowIfNull(mailAccounts);
        await ReloadTemplatesAsync();
        var answers = await Task.WhenAll(mailAccounts.Select(a => Task.Run(async () =>
        {
            try
            {
                var (reply, features) = await mail.GetAutoReplyAsync(a.Connection);
                var template = await AutoReplyText.TemplateForAsync(settings, a.Connection.Id, reply);
                return (a.Account, a.Connection, State: (Reply: reply, Features: features, Template: template), Supported: (bool?)true);
            }
            catch (AutoReplyUnavailableException ex)
            {
                logger.LogInformation("Out-of-office reply of {Account} not available: {Reason}", a.Account.Title, ex.Message);
                return (a.Account, a.Connection, State: default((AutoReply, AutoReplyFeatures, string)), Supported: (bool?)false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Out-of-office status of {Account} could not be read", a.Account.Title);
                return (a.Account, a.Connection, State: default((AutoReply, AutoReplyFeatures, string)), Supported: (bool?)null);
            }
        })));

        var rows = new List<AutoReplyAccountViewModel>();
        foreach (var (account, connection, state, supported) in answers)
        {
            var existing = Accounts.FirstOrDefault(r => r.Connection.Id == connection.Id);
            switch (supported)
            {
                case true:
                    var row = existing ?? new AutoReplyAccountViewModel(this, mail, settings, account, connection);
                    row.Show(state.Item1, state.Item2, state.Item3);
                    rows.Add(row);
                    break;
                case null when existing is not null:
                    rows.Add(existing);
                    break;
            }
        }

        Accounts.Clear();
        foreach (var row in rows)
        {
            Accounts.Add(row);
        }

        OnChanged();
    }

    /// <summary>Saved in the full dialog: counts at once (no second trip to the server); a new account joins the list.</summary>
    public void Apply(Account account, ServiceConnection connection, AutoReply reply, AutoReplyFeatures features, string template)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(reply);
        var row = Accounts.FirstOrDefault(r => r.Connection.Id == connection.Id);
        if (row is null)
        {
            row = new AutoReplyAccountViewModel(this, mail, settings, account, connection);
            Accounts.Add(row);
        }

        row.Show(reply, features, template);
        OnChanged();
    }

    internal void OnChanged()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(AnyActive));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void RequestEdit(AutoReplyAccountViewModel row) => EditRequested?.Invoke(this, row);
}

/// <summary>One account in the out-of-office list: switch and period, saved as soon as they change.</summary>
internal sealed partial class AutoReplyAccountViewModel(AutoRepliesViewModel owner, MailController mail, ISettingsStore settings, Account account, ServiceConnection connection) : ObservableObject
{
    private bool _showing;

    public Account Account { get; } = account;

    public ServiceConnection Connection { get; } = connection;

    public string Title => Account.Title;

    public AutoReplyFeatures Features { get; private set; }

    public bool CanSchedule => Features.HasFlag(AutoReplyFeatures.Schedule);

    /// <summary>What the server has now.</summary>
    public AutoReply Reply { get; private set; } = AutoReply.Off;

    /// <summary>The text with {{start}}/{{end}} (see <see cref="AutoReplyText"/>).</summary>
    public string Template { get; private set; } = string.Empty;

    public bool IsActive => Reply.IsActive(DateTimeOffset.Now);

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>"Nur vom … bis und mit …".</summary>
    [ObservableProperty]
    public partial bool IsScheduled { get; set; }

    [ObservableProperty]
    public partial DateTime? FirstDay { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial DateTime? LastDay { get; set; } = DateTime.Today.AddDays(7);

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public ObservableCollection<AutoReplyTemplate> Templates => owner.Templates;

    /// <summary>The named text in use (if the text is one of them); choosing one takes its text over at once.</summary>
    [ObservableProperty]
    public partial AutoReplyTemplate? SelectedTemplate { get; set; }

    internal void MatchTemplate()
    {
        _showing = true;
        try
        {
            SelectedTemplate = Templates.FirstOrDefault(t => t.Text.Trim() == Template.Trim());
        }
        finally
        {
            _showing = false;
        }
    }

    partial void OnSelectedTemplateChanged(AutoReplyTemplate? value)
    {
        if (_showing || value is null)
        {
            return;
        }

        Template = value.Text;
        _subject = value.Subject;
        if (IsEnabled)
        {
            _ = SaveAsync();
        }
        else
        {
            _ = AutoReplyText.RememberAsync(settings, Connection.Id, Template); // the next switch-on uses it
        }
    }

    // A subject chosen with a template; otherwise the one the server has.
    private string? _subject;

    public string Status => Reply switch
    {
        { IsEnabled: false } => T("aus"),
        { End: { } end } when end <= DateTimeOffset.Now => T("abgelaufen"),
        { End: { } end } => F("aktiv, bis und mit {0:d}", end.LocalDateTime.Date.AddDays(-1)),
        _ => T("aktiv"),
    };

    /// <summary>Shows what the server has (without saving anything).</summary>
    public void Show(AutoReply reply, AutoReplyFeatures features, string template)
    {
        _showing = true;
        try
        {
            Reply = reply;
            Features = features;
            Template = template.Length > 0 ? template : AutoReplyText.Default(Account.DisplayName);
            _subject = reply.Subject;
            SelectedTemplate = Templates.FirstOrDefault(t => t.Text.Trim() == Template.Trim());
            IsEnabled = reply.IsEnabled;
            IsScheduled = reply.IsScheduled && CanSchedule;
            if (reply.IsScheduled)
            {
                FirstDay = reply.Start!.Value.LocalDateTime.Date;
                LastDay = reply.End!.Value.LocalDateTime.Date.AddDays(-1);
            }

            Error = null;
            OnPropertyChanged(nameof(CanSchedule));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(Status));
        }
        finally
        {
            _showing = false;
        }
    }

    partial void OnIsEnabledChanged(bool value) => _ = SaveAsync();

    partial void OnIsScheduledChanged(bool value)
    {
        if (IsEnabled)
        {
            _ = SaveAsync();
        }
    }

    partial void OnFirstDayChanged(DateTime? value)
    {
        if (IsEnabled && IsScheduled)
        {
            _ = SaveAsync();
        }
    }

    partial void OnLastDayChanged(DateTime? value)
    {
        if (IsEnabled && IsScheduled)
        {
            _ = SaveAsync();
        }
    }

    [RelayCommand]
    private void MoreOptions() => owner.RequestEdit(this);

    private async Task SaveAsync()
    {
        if (_showing)
        {
            return;
        }

        Error = null;
        DateTimeOffset? start = null, end = null;
        if (IsScheduled && CanSchedule)
        {
            if (FirstDay is not { } first || LastDay is not { } last || last.Date < first.Date)
            {
                Error = T("Bitte einen gültigen Zeitraum wählen (der letzte Tag nicht vor dem ersten).");
                return;
            }

            start = new DateTimeOffset(first.Date, TimeZoneInfo.Local.GetUtcOffset(first.Date));
            end = new DateTimeOffset(last.Date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(last.Date.AddDays(1)));
        }

        var previous = Reply;
        try
        {
            var reply = new AutoReply(IsEnabled, AutoReplyText.ForServer(Template, IsEnabled, start, end), start, end, Features.HasFlag(AutoReplyFeatures.Subject) ? _subject : null);
            IsBusy = true;
            await Task.Run(() => mail.SetAutoReplyAsync(Account, Connection, reply));
            await AutoReplyText.RememberAsync(settings, Connection.Id, Template);
            Reply = reply;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(Status));
            owner.OnChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not saved: the switch shows the server's state again, the reason stays below it.
            Show(previous, Features, Template);
            Error = ex is ArgumentException or AutoReplyUnavailableException ? ex.Message : F("Die Abwesenheitsnotiz konnte nicht gespeichert werden: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
