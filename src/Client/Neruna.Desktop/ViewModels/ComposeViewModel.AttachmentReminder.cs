using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Anhang-Erinnerung (Einstellungen → Schreiben): a word like "Anhang" or ".pdf" in the own text while nothing is
/// attached shows a bar below the text, and sending asks first. Closing the bar ends it for this mail.
/// </summary>
internal sealed partial class ComposeViewModel
{
    private DispatcherTimer? _reminderTimer;
    private int _reminderCheck;
    private bool _reminderDismissed;
    private bool _askingAttachment;

    /// <summary>The words and whether to remind; null: no reminder (tests).</summary>
    public AttachmentReminderService? AttachmentReminder { get; init; }

    /// <summary>Asks "trotzdem ohne Anhang senden?" (true: send); replaced in tests.</summary>
    public Func<Task<bool>> ConfirmSendWithoutAttachment { get; init; } = () =>
        Views.ChoiceDialog.ShowInFrontAsync(T("Anhang fehlt?"), T("Es scheint ein Anhang zu fehlen, trotzdem ohne Anhang senden?"),
            (T("Ja"), true, false), (T("Nein"), false, true));

    /// <summary>"Im Text steht «Anhang», aber es ist nichts angehängt." – null hides the bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingAttachment))]
    public partial string? MissingAttachmentHint { get; set; }

    public bool HasMissingAttachment => MissingAttachmentHint is not null;

    /// <summary>The bar's ×: no more reminding for this mail (also not when sending).</summary>
    [RelayCommand]
    private void DismissAttachmentReminder()
    {
        _reminderDismissed = true;
        MissingAttachmentHint = null;
    }

    /// <summary>Checks a moment after typing stops (not on every key).</summary>
    private void ScheduleReminderCheck()
    {
        if (AttachmentReminder is null || _reminderDismissed)
        {
            return;
        }

        if (_reminderTimer is null)
        {
            _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _reminderTimer.Tick += (_, _) =>
            {
                _reminderTimer.Stop();
                _ = UpdateAttachmentReminderAsync();
            };
        }

        _reminderTimer.Stop();
        _reminderTimer.Start();
    }

    private async Task UpdateAttachmentReminderAsync()
    {
        var version = ++_reminderCheck;
        string? keyword;
        try
        {
            keyword = await MissingAttachmentKeywordAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            keyword = null; // the editor went away meanwhile
        }

        if (version == _reminderCheck)
        {
            MissingAttachmentHint = keyword is null ? null : F("Im Text steht «{0}», aber es ist nichts angehängt.", keyword);
        }
    }

    /// <summary>The word that announces an attachment, while nothing is attached and the reminder is on; otherwise null.</summary>
    private async Task<string?> MissingAttachmentKeywordAsync()
    {
        if (AttachmentReminder is not { } reminder || _reminderDismissed || Attachments.Count > 0 || _editor is not { } editor)
        {
            return null;
        }

        var keywords = await reminder.GetActiveKeywordsAsync();
        if (keywords.Count == 0)
        {
            return null;
        }

        var texts = await editor.GetTextsAsync();
        return AttachmentReminderService.Find(editor.IsPlainText ? AttachmentReminderService.OwnPlainText(texts.Own) : texts.Own, keywords);
    }

    /// <summary>Before sending: false when an attachment seems to be missing and the user chose "Nein".</summary>
    private async Task<bool> MaySendWithoutAttachmentAsync()
    {
        if (_askingAttachment)
        {
            return false;
        }

        string? keyword;
        try
        {
            keyword = await MissingAttachmentKeywordAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true; // never keep a mail back because the check failed
        }

        if (keyword is null)
        {
            return true;
        }

        MissingAttachmentHint = F("Im Text steht «{0}», aber es ist nichts angehängt.", keyword);
        _askingAttachment = true;
        try
        {
            if (!await ConfirmSendWithoutAttachment())
            {
                return false;
            }
        }
        finally
        {
            _askingAttachment = false;
        }

        // Decided: a second "Senden" (e.g. after the encryption question) does not ask again.
        DismissAttachmentReminder();
        return true;
    }
}
