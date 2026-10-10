using Microsoft.Extensions.Logging;
using Neruna.Core;
using Neruna.Core.Mail;
using Neruna.Desktop.ViewModels;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// Notifications for new mail (setting "Benachrichtigungen"): one per message, a summary from four on – none while the
/// user looks at exactly that folder in Neruna. A click opens the message (the shell decides how).
/// </summary>
internal sealed class MailNotifier(NotificationService notifications, ISettingsStore settings, ILogger<MailNotifier> logger)
{
    private const int MaxNotifications = 3;

    /// <param name="isOpenInFront">Whether the folder is open in Neruna, in front – then nothing is shown.</param>
    /// <param name="open">Opens a message of the folder (a click on the notification).</param>
    public async Task NotifyAsync(NewMailEvent e, Func<MailFolder, bool> isOpenInFront, Func<MailFolder, string, Task> open)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(isOpenInFront);
        ArgumentNullException.ThrowIfNull(open);
        try
        {
            var account = e.Account.Title;
            if (!await settings.GetBoolAsync(SettingKeys.MailNotifications, fallback: true))
            {
                logger.LogInformation("{Count} new mail(s) for {Account}: notifications are switched off", e.Messages.Count, account);
                return;
            }

            if (isOpenInFront(e.Folder))
            {
                logger.LogInformation("{Count} new mail(s) for {Account}: no notification, the inbox is open in front", e.Messages.Count, account);
                return;
            }

            logger.LogInformation("{Count} new mail(s) for {Account}: showing a notification", e.Messages.Count, account);
            if (e.Messages.Count > MaxNotifications)
            {
                var newest = e.Messages[0];
                notifications.Show(new NotificationViewModel(
                    F("{0} neue E-Mails · {1}", e.Messages.Count, account),
                    string.Join(", ", e.Messages.Select(m => m.From?.DisplayText).Where(n => n is not null).Distinct().Take(3)),
                    newest.Subject,
                    null,
                    () => open(e.Folder, newest.RemoteId)));
                return;
            }

            foreach (var message in e.Messages.Reverse())
            {
                notifications.Show(new NotificationViewModel(
                    T("Neue E-Mail · ") + account,
                    message.From?.DisplayText ?? T("(unbekannt)"),
                    string.IsNullOrWhiteSpace(message.Subject) ? T("(kein Betreff)") : message.Subject,
                    message.Preview,
                    () => open(e.Folder, message.RemoteId)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Showing the notification failed");
        }
    }

    /// <summary>"Test-Benachrichtigung" in the settings.</summary>
    /// <returns>Null when shown, otherwise what went wrong.</returns>
    public string? ShowTest()
    {
        try
        {
            notifications.Show(new NotificationViewModel(
                T("Neue E-Mail · Test"), "Neruna", T("So sehen Benachrichtigungen aus"), T("Ein Klick holt Neruna nach vorne."),
                () =>
                {
                    NotificationService.ActivateMainWindow();
                    return Task.CompletedTask;
                }));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Showing the test notification failed");
            return ex.Message;
        }
    }
}
