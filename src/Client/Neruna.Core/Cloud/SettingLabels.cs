using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Cloud;

/// <summary>
/// What a stored setting is about, in words the user knows from the settings ("Datenschutz", "Kalenderfarben") instead
/// of its key ("privacy.blockTrackers", "calendar.color.…"). Several keys share a label; lists show it once.
/// </summary>
public static class SettingLabels
{
    // Longest prefix first: "calendar.color." before "calendar.".
    private static readonly (string Prefix, string Label)[] Labels =
    [
        ("calendar.color.", "Kalenderfarben"),
        ("contacts.color.", "Adressbuchfarben"),
        ("calendar.name.", "Namen von Kalendern und Adressbüchern"),
        ("addressbook.name.", "Namen von Kalendern und Adressbüchern"),
        ("calendars.", "Angezeigte Kalender und Adressbücher"),
        ("calendar.hidden", "Angezeigte Kalender und Adressbücher"),
        ("addressbooks.", "Angezeigte Kalender und Adressbücher"),
        ("contacts.hidden", "Angezeigte Kalender und Adressbücher"),
        ("calendar.planning", "Kalender: Planungsansicht"),
        ("calendar.default", "Kalender: Standardwerte"),
        ("calendar.deleteAnsweredInvitations", "Kalender: Standardwerte"),
        ("invitations.", "Kalender: Standardwerte"),
        ("calendar.", "Kalender: Ansicht"),
        ("tasks.", "Aufgaben"),
        ("mail.offline.", "Offline verfügbare E-Mails"),
        ("mail.favorites", "Favoriten"),
        ("mail.quickActions", "Schnellaktionen"),
        ("mail.remoteContentAllowed", "Datenschutz"),
        ("privacy.", "Datenschutz"),
        ("mail.notifications", "Benachrichtigungen"),
        ("mail.push", "Benachrichtigungen"),
        ("mail.", "E-Mail: Allgemein"),
        ("compose.", "E-Mail schreiben"),
        ("signature.", "Signaturen: Zuordnung zu den Konten"),
        ("smime.", "Zertifikate (S/MIME)"),
        ("autoreply.", "Abwesenheitsnotizen"),
        ("workspaces", "Workspaces"),
        ("ui.language", "Sprache"),
        ("ui.navigation", "Linke Leiste"),
        ("ui.", "Darstellung"),
        ("appearance.", "Darstellung"),
        ("search.", "Suche"),
        ("crash.", "Absturzberichte"),
        ("tls.", "Verbindungen"),
        ("ews.", "Verbindungen"),
    ];

    /// <summary>The label of a setting key, translated; "Weitere Einstellungen" for keys without one.</summary>
    public static string Of(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var (prefix, label) in Labels)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return T(label);
            }
        }

        return T("Weitere Einstellungen");
    }
}
