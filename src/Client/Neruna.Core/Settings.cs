using System.Globalization;

namespace Neruna.Core;

/// <summary>Simple key/value preferences that are not tied to an account.</summary>
public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}

public static class SettingKeys
{
    /// <summary>Sign automatically when an own certificate for the sender exists (default: on).</summary>
    public const string AutoSign = "smime.autoSign";

    /// <summary>Encrypt automatically when certificates of all recipients are known (default: on).</summary>
    public const string AutoEncrypt = "smime.autoEncrypt";

    /// <summary>"true": new mails, replies and forwards open in their own window instead of the reading pane.</summary>
    public const string ComposeInWindow = "compose.openInWindow";

    /// <summary>Default font family for new mails.</summary>
    public const string ComposeFont = "compose.font";

    /// <summary>Default font size in points for new mails.</summary>
    public const string ComposeFontSize = "compose.fontSize";

    /// <summary>When an opened message is marked as read, see <see cref="MarkAsReadMode"/>.</summary>
    public const string MarkAsRead = "mail.markAsRead";

    /// <summary>Folder tree nodes the user collapsed (JSON list of keys); everything else is expanded.</summary>
    public const string CollapsedFolders = "mail.collapsedFolders";

    /// <summary>New mail in the inbox right away (IMAP IDLE; servers without it are asked every 2 minutes). Default: on.</summary>
    public const string MailPush = "mail.push";

    /// <summary>A desktop notification for new mail while Neruna is not the active window. Default: on.</summary>
    public const string MailNotifications = "mail.notifications";

    /// <summary>Address books hidden in the contact list (JSON list of keys).</summary>
    public const string HiddenAddressBooks = "contacts.hidden";

    /// <summary>How many months the date navigator in the calendar shows (1 or 2).</summary>
    public const string CalendarNavigatorMonths = "calendar.navigatorMonths";

    /// <summary>Week view: "list" (events per day, default) or "timegrid" (hourly time grid).</summary>
    public const string CalendarLayout = "calendar.layout";

    /// <summary>Time grid subdivision in minutes: 60, 30 (default) or 15.</summary>
    public const string CalendarGridMinutes = "calendar.gridMinutes";

    /// <summary>"true": the week view shows Monday–Friday only.</summary>
    public const string CalendarWorkWeek = "calendar.workWeek";

    /// <summary>Reminder for new events and accepted invitations, minutes before the start; "none" for none. Default: 15.</summary>
    public const string CalendarDefaultReminder = "calendar.defaultReminder";

    /// <summary>Toolbar buttons with text below the icon (default) or icon only.</summary>
    public const string ToolbarLabels = "ui.toolbarLabels";

    /// <summary>"System", "Light" or "Dark".</summary>
    public const string ThemeMode = "appearance.theme";

    /// <summary>"Blue", "Red" or "Green".</summary>
    public const string ColorScheme = "appearance.colorScheme";
}

/// <summary>When opening a message marks it as read (setting "E-Mail → Als gelesen markieren").</summary>
public enum MarkAsReadMode
{
    OnSelect,
    AfterDelay,
    OnReply,
    Never,
}

public static class SettingsStoreExtensions
{
    public static async Task<bool> GetBoolAsync(this ISettingsStore store, string key, bool fallback = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return bool.TryParse(await store.GetAsync(key, cancellationToken), out var value) ? value : fallback;
    }

    /// <summary>The reminder new events and accepted invitations get (Einstellungen → Kalender); null for none.</summary>
    public static async Task<int?> GetDefaultReminderAsync(this ISettingsStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var value = await store.GetAsync(SettingKeys.CalendarDefaultReminder, cancellationToken);
        return value == "none" ? null
            : int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var minutes) && minutes >= 0 ? minutes
            : Calendar.EventDraft.DefaultReminderMinutes;
    }

    public static Task SetDefaultReminderAsync(this ISettingsStore store, int? minutes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.SetAsync(SettingKeys.CalendarDefaultReminder, minutes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none", cancellationToken);
    }

    public static Task SetBoolAsync(this ISettingsStore store, string key, bool value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.SetAsync(key, value.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }
}
