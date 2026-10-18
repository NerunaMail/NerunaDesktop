using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>One recipient who gets PDFs only with a password (the password itself stays in the keychain).</summary>
internal sealed record PdfRuleRow(PdfProtectionRule Rule)
{
    public string Recipient => Rule.Recipient;

    public string Mode => Rule.Automatic ? T("automatisch beim Senden") : T("Hinweis beim Verfassen");
}

/// <summary>
/// Settings → PDF: the stamps (shared with the quick actions, which export them along) and PDF security – the offer to
/// protect PDF attachments with a password and the recipients who get them only protected.
/// </summary>
internal sealed partial class PdfOptionsViewModel(QuickActionsViewModel quickActions, PdfProtectionService security) : ViewModelBase
{
    private bool _loading;

    public QuickActionsViewModel Stamps => quickActions;

    [ObservableProperty]
    public partial bool OfferProtection { get; set; } = true;

    partial void OnOfferProtectionChanged(bool value)
    {
        if (!_loading)
        {
            _ = security.SetOfferAsync(value);
        }
    }

    public ObservableCollection<PdfRuleRow> Rules { get; } = [];

    public bool HasRules => Rules.Count > 0;

    /// <summary>A new rule: "anna@treuhand.example" or "@treuhand.example".</summary>
    [ObservableProperty]
    public partial string NewRecipient { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool NewAutomatic { get; set; }

    [ObservableProperty]
    public partial bool ShowNewPassword { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public async Task LoadAsync()
    {
        _loading = true;
        OfferProtection = await security.GetOfferAsync();
        _loading = false;
        await quickActions.ReloadAsync();
        await ReloadRulesAsync();
    }

    [RelayCommand]
    private void SuggestPassword()
    {
        NewPassword = PdfProtection.NewPassword();
        ShowNewPassword = true;
    }

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        Error = null;
        var recipient = NewRecipient.Trim();
        var valid = recipient.StartsWith('@')
            ? recipient.Length > 3 && recipient.IndexOf('.', StringComparison.Ordinal) > 1 && !recipient[1..].Contains('@', StringComparison.Ordinal)
            : MimeKit.MailboxAddress.TryParse(recipient, out var mailbox) && mailbox.Address == recipient;
        if (!valid)
        {
            Error = T("Bitte eine E-Mail-Adresse oder eine Domain mit @ angeben, z. B. anna@treuhand.example oder @treuhand.example.");
            return;
        }

        if (NewPassword.Length < 6)
        {
            Error = T("Das Kennwort sollte mindestens 6 Zeichen haben.");
            return;
        }

        var existing = Rules.FirstOrDefault(r => string.Equals(r.Recipient, recipient, StringComparison.OrdinalIgnoreCase))?.Rule;
        await security.SaveRuleAsync(new PdfProtectionRule(existing?.Id ?? Guid.NewGuid(), recipient, NewAutomatic), NewPassword);
        NewRecipient = string.Empty;
        NewPassword = string.Empty;
        NewAutomatic = false;
        ShowNewPassword = false;
        await ReloadRulesAsync();
    }

    [RelayCommand]
    private async Task RemoveRuleAsync(PdfRuleRow? row)
    {
        if (row is not null)
        {
            await security.RemoveRuleAsync(row.Rule);
            await ReloadRulesAsync();
        }
    }

    private async Task ReloadRulesAsync()
    {
        Rules.Clear();
        foreach (var rule in (await security.GetRulesAsync()).OrderBy(r => r.Recipient.TrimStart('@'), StringComparer.OrdinalIgnoreCase))
        {
            Rules.Add(new PdfRuleRow(rule));
        }

        OnPropertyChanged(nameof(HasRules));
    }
}
