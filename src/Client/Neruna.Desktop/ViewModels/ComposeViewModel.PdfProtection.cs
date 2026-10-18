using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// PDF attachments with a password (Einstellungen → PDF): each unprotected PDF offers "Schützen"; for recipients with a
/// stored password a bar says so and protects with it in one click, or – set to "automatisch" – when sending.
/// </summary>
internal sealed partial class ComposeViewModel
{
    private IReadOnlyList<ComposeAttachment> _passwordFor = [];
    private int _pdfCheck;

    /// <summary>Rules and the offer to protect; null: no PDF protection (tests).</summary>
    public PdfProtectionService? PdfSecurity
    {
        get;
        init
        {
            field = value;
            _ = CheckPdfProtectionAsync();
        }
    }

    /// <summary>"Für anna@treuhand.example nur geschützte PDFs" – with the stored password one click away.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPdfHint))]
    public partial string? PdfHint { get; set; }

    public bool HasPdfHint => PdfHint is not null;

    private PdfProtectionRule? _hintRule;

    /// <summary>The password panel is open (for one PDF or all unprotected ones).</summary>
    [ObservableProperty]
    public partial bool IsAskingPdfPassword { get; set; }

    [ObservableProperty]
    public partial string PdfPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowPdfPassword { get; set; }

    [ObservableProperty]
    public partial bool IsProtectingPdf { get; set; }

    private IEnumerable<ComposeAttachment> UnprotectedPdfs => Attachments.Where(a => a.IsPdf && !a.IsProtected);

    private IEnumerable<string> RecipientAddresses =>
        new[] { To, Cc, Bcc }.SelectMany(text => InternetAddressList.TryParse(text.Replace(';', ','), out var list) ? list.Mailboxes.Select(m => m.Address) : []);

    /// <summary>Checks new attachments (password or not) and which recipients want protected PDFs.</summary>
    private async Task CheckPdfProtectionAsync()
    {
        var version = ++_pdfCheck;
        var offer = PdfSecurity is { } security && await security.GetOfferAsync();
        foreach (var attachment in Attachments.Where(a => a.IsPdf).ToList())
        {
            if (!attachment.IsChecked && await ContentAsync(attachment) is { } data)
            {
                attachment.IsChecked = true;
                attachment.IsProtected = await Task.Run(() => PdfPages.NeedsPassword(data));
            }

            attachment.OffersProtection = offer;
        }

        var rules = PdfSecurity is { } service && UnprotectedPdfs.Any() ? await service.MatchAsync(RecipientAddresses) : [];
        if (version != _pdfCheck)
        {
            return;
        }

        var first = rules.FirstOrDefault(r => !r.Rule.Automatic);
        _hintRule = first.Rule;
        PdfHint = first.Rule is null ? null
            : rules.Count(r => !r.Rule.Automatic) == 1 ? F("Für {0} sollen PDFs nur mit Kennwort verschickt werden.", first.Recipient)
            : F("Für {0} und weitere Empfänger sollen PDFs nur mit Kennwort verschickt werden.", first.Recipient);
    }

    /// <summary>The bar: all unprotected PDFs with the stored password of the recipient.</summary>
    [RelayCommand]
    private async Task ProtectWithStoredPasswordAsync()
    {
        if (_hintRule is { } rule && PdfSecurity is { } security)
        {
            if (await security.GetPasswordAsync(rule) is { Length: > 0 } password)
            {
                await ProtectAsync([.. UnprotectedPdfs], password);
            }
            else
            {
                Error = F("Für «{0}» ist kein Kennwort hinterlegt (Einstellungen → PDF).", rule.Recipient);
            }
        }
    }

    /// <summary>The bar's "Anderes Kennwort …": the panel for all unprotected PDFs.</summary>
    [RelayCommand]
    private void ProtectWithOtherPassword() => AskPassword([.. UnprotectedPdfs]);

    /// <summary>The lock on one PDF: the panel, with the recipient's password if there is one, otherwise a new one.</summary>
    [RelayCommand]
    private async Task ProtectAttachmentAsync(ComposeAttachment? attachment)
    {
        if (attachment is null)
        {
            return;
        }

        AskPassword([attachment]);
        if (_hintRule is { } rule && PdfSecurity is { } security && await security.GetPasswordAsync(rule) is { Length: > 0 } stored)
        {
            PdfPassword = stored;
        }
    }

    private void AskPassword(IReadOnlyList<ComposeAttachment> attachments)
    {
        _passwordFor = attachments;
        PdfPassword = PdfProtection.NewPassword();
        ShowPdfPassword = true;
        IsAskingPdfPassword = attachments.Count > 0;
    }

    [RelayCommand]
    private async Task ConfirmPdfPasswordAsync()
    {
        if (string.IsNullOrEmpty(PdfPassword))
        {
            return;
        }

        if (await ProtectAsync(_passwordFor, PdfPassword))
        {
            IsAskingPdfPassword = false;
            PdfPassword = string.Empty;
        }
    }

    [RelayCommand]
    private void CancelPdfPassword()
    {
        IsAskingPdfPassword = false;
        PdfPassword = string.Empty;
        _passwordFor = [];
    }

    /// <summary>
    /// Before sending: recipients set to "automatisch" get the PDFs protected with their password. Different passwords
    /// for different recipients cannot go into one mail: then the user decides.
    /// </summary>
    /// <returns>False: do not send (the reason is in <see cref="Error"/>).</returns>
    private async Task<bool> ProtectPdfsForSendingAsync()
    {
        if (PdfSecurity is not { } security || !UnprotectedPdfs.Any())
        {
            return true;
        }

        var automatic = (await security.MatchAsync(RecipientAddresses)).Where(m => m.Rule.Automatic).ToList();
        if (automatic.Count == 0)
        {
            return true;
        }

        var passwords = new List<string>();
        foreach (var (recipient, rule) in automatic)
        {
            if (await security.GetPasswordAsync(rule) is not { Length: > 0 } password)
            {
                Error = F("Für «{0}» ist kein Kennwort hinterlegt (Einstellungen → PDF).", recipient);
                return false;
            }

            passwords.Add(password);
        }

        if (passwords.Distinct(StringComparer.Ordinal).Count() > 1)
        {
            Error = F("Für {0} sind verschiedene Kennwörter hinterlegt – bitte die PDFs von Hand schützen oder getrennt senden.",
                string.Join(", ", automatic.Select(a => a.Recipient)));
            return false;
        }

        return await ProtectAsync([.. UnprotectedPdfs], passwords[0]);
    }

    // Each PDF is replaced by its protected copy (same name, same place in the list).
    private async Task<bool> ProtectAsync(IReadOnlyList<ComposeAttachment> attachments, string password)
    {
        IsProtectingPdf = true;
        try
        {
            foreach (var attachment in attachments.Where(a => Attachments.Contains(a) && !a.IsProtected))
            {
                if (await ContentAsync(attachment) is not { } data)
                {
                    continue;
                }

                var locked = await Task.Run(() => PdfProtection.Protect(data, password));
                var replacement = new ComposeAttachment(attachment.FileName, new BodyBuilder().Attachments.Add(attachment.FileName, locked, new ContentType("application", "pdf")))
                {
                    IsChecked = true,
                    IsProtected = true,
                    OffersProtection = attachment.OffersProtection,
                };
                Attachments[Attachments.IndexOf(attachment)] = replacement;
            }

            return true;
        }
        catch (PdfProtectionException ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            IsProtectingPdf = false;
        }
    }

    private static async Task<byte[]?> ContentAsync(ComposeAttachment attachment)
    {
        if (attachment.Entity is not MimePart { Content: { } content })
        {
            return null;
        }

        using var data = new MemoryStream();
        await content.DecodeToAsync(data);
        return data.ToArray();
    }
}
