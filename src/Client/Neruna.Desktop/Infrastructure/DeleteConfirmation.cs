using Neruna.Core;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Infrastructure;

/// <summary>What is deleted for good (on the server too), each with its own "Nicht mehr fragen".</summary>
internal enum DeleteKind
{
    Contact,
    Event,
    Task,
}

/// <summary>
/// The one question before something is gone for good: contacts, events and tasks ask with "Nicht mehr fragen" (undone
/// under Einstellungen → Daten); what has to be decided each time (absagen, mail deleted for good) asks always. Mail into
/// the trash asks nothing.
/// </summary>
internal static class DeleteConfirmation
{
    /// <summary>Set by the shell at start; without it every question is asked (and nothing is remembered).</summary>
    public static ISettingsStore? Settings { get; set; }

    public static string KeyOf(DeleteKind kind) => kind switch
    {
        DeleteKind.Contact => SettingKeys.ConfirmDeleteContacts,
        DeleteKind.Event => SettingKeys.ConfirmDeleteEvents,
        _ => SettingKeys.ConfirmDeleteTasks,
    };

    /// <summary>Whether to delete: at once when the user said not to ask any more, else after the question.</summary>
    /// <param name="message">What is deleted, e.g. "«Lea Keller» wird auch auf dem Server gelöscht."</param>
    public static async Task<bool> AskAsync(DeleteKind kind, string title, string message)
    {
        if (Settings is { } settings && !await settings.GetBoolAsync(KeyOf(kind), fallback: true))
        {
            return true;
        }

        var checkText = kind switch
        {
            DeleteKind.Contact => T("Beim Löschen von Kontakten nicht mehr fragen"),
            DeleteKind.Event => T("Beim Löschen von Terminen nicht mehr fragen"),
            _ => T("Beim Löschen von Aufgaben nicht mehr fragen"),
        };
        var (delete, dontAsk) = await Views.ChoiceDialog.ShowWithCheckInFrontAsync(title, message, checkText,
            (T("Löschen"), true, false), (T("Abbrechen"), false, true));
        if (delete && dontAsk && Settings is { } store)
        {
            await store.SetBoolAsync(KeyOf(kind), false);
        }

        return delete;
    }

    /// <summary>Asked every time, without "Nicht mehr fragen" (an answer goes out, mail is gone for good).</summary>
    public static Task<bool> AskAlwaysAsync(string title, string message, string deleteText) =>
        Views.ChoiceDialog.ShowInFrontAsync(title, message, (deleteText, true, false), (T("Abbrechen"), false, true));
}
