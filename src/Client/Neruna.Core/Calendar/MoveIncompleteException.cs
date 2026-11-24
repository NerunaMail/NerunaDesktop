using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Calendar;

/// <summary>
/// Moving an event or task to another calendar or list: it is saved there (<see cref="Saved"/>), but the original could
/// not be removed from <see cref="Source"/> – it now exists twice. Saving is done; the editor must close (saving again
/// would make a third copy) and tell the user to delete the old one.
/// </summary>
public sealed class MoveIncompleteException(CalendarObject saved, CalendarInfo source, Exception inner)
    : Exception(F("Gespeichert, aber das Original in «{0}» konnte nicht gelöscht werden ({1}). Bitte dort von Hand löschen.", source.Name, inner.Message), inner)
{
    public CalendarObject Saved { get; } = saved;

    public CalendarInfo SourceCalendar { get; } = source;
}
