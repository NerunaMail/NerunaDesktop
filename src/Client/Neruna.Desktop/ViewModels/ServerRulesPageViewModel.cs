using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Einstellungen → Serverregeln: the mail accounts on the left (with how many rules each has), the rules of the chosen
/// one on the right – the rules the server applies to new mail, easy to find instead of hidden in an account's menu.
/// </summary>
internal sealed partial class ServerRulesPageViewModel(IAccountStore accounts, MailController mail) : ViewModelBase
{
    public ObservableCollection<RulesAccountItem> Accounts { get; } = [];

    public bool HasAccounts => Accounts.Count > 0;

    [ObservableProperty]
    public partial RulesAccountItem? SelectedAccount { get; set; }

    /// <summary>The rules of the chosen account (the same editor as before, on the page).</summary>
    [ObservableProperty]
    public partial ServerRulesViewModel? Current { get; set; }

    /// <summary>Unsaved changes: the account stays until they are saved or discarded.</summary>
    public bool CanSwitch => Current is not { IsDirty: true };

    private Guid? _wanted;
    private bool _loading;

    /// <summary>Opens the page on this account (from the folder list's "Serverregeln …" or an account card).</summary>
    public async Task ShowAsync(Guid accountId)
    {
        _wanted = accountId;
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (Current is { IsDirty: true })
        {
            return; // keep the editor with its changes
        }

        _loading = true;
        try
        {
            Accounts.Clear();
            foreach (var account in await accounts.GetAccountsAsync())
            {
                if (account.ConnectionsOf(ServiceKind.Mail).FirstOrDefault() is { } connection
                    && connection.ProviderId != Neruna.Core.Providers.ProviderIds.Pst)
                {
                    Accounts.Add(new RulesAccountItem(account, connection));
                }
            }

            OnPropertyChanged(nameof(HasAccounts));
        }
        finally
        {
            _loading = false;
        }

        SelectedAccount = Accounts.FirstOrDefault(a => a.Account.Id == _wanted) ?? Accounts.FirstOrDefault();
        _wanted = null;

        // The counters: every server asked at once (the chosen account's comes with its rules).
        foreach (var item in Accounts.Where(a => a != SelectedAccount))
        {
            _ = CountAsync(item);
        }
    }

    private async Task CountAsync(RulesAccountItem item)
    {
        try
        {
            var set = await Task.Run(() => mail.GetServerRulesAsync(item.Connection));
            item.Show(set.Rules.Count + set.Others.Count, null);
        }
        catch (Exception ex) when (ex is ServerRulesUnavailableException or HttpRequestException or IOException or InvalidOperationException)
        {
            item.Show(null, ex.Message);
        }
    }

    partial void OnSelectedAccountChanged(RulesAccountItem? value)
    {
        if (_loading)
        {
            return;
        }

        if (Current is not null)
        {
            Current.PropertyChanged -= OnCurrentChanged;
        }

        Current = value is null ? null : new ServerRulesViewModel(mail, value.Account, value.Connection) { IsEmbedded = true };
        if (Current is { } rules && value is not null)
        {
            rules.PropertyChanged += OnCurrentChanged;
            rules.Rules.CollectionChanged += (_, _) => value.Show(rules.Count, null);
            _ = LoadCurrentAsync(rules, value);
        }

        OnPropertyChanged(nameof(CanSwitch));
    }

    private static async Task LoadCurrentAsync(ServerRulesViewModel rules, RulesAccountItem item)
    {
        await rules.LoadAsync();
        item.Show(rules.Error is null ? rules.Count : null, rules.Error);
    }

    private void OnCurrentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerRulesViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(CanSwitch));
        }
    }
}

/// <summary>An account on the left of Einstellungen → Serverregeln, with its number of rules.</summary>
internal sealed partial class RulesAccountItem(Account account, ServiceConnection connection) : ObservableObject
{
    public Account Account { get; } = account;

    public ServiceConnection Connection { get; } = connection;

    public string Title => Account.Title;

    /// <summary>"3", "…" while asking, "–" when the server offers no rules (the reason in <see cref="Problem"/>).</summary>
    [ObservableProperty]
    public partial string CountText { get; set; } = "…";

    [ObservableProperty]
    public partial string? Problem { get; set; }

    internal void Show(int? count, string? problem)
    {
        CountText = count?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "–";
        Problem = count is null ? problem ?? T("Keine Serverregeln möglich") : null;
    }
}
