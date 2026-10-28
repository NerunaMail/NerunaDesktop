using System.Text.Json;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Calendar;

/// <summary>
/// A category of events ("Besprechung", "Kunde" …): name and colour. On the server only the name is stored – iCalendar
/// CATEGORIES (CalDAV), the categories of Microsoft 365 and Exchange – so other programs see it and Neruna matches by name.
/// </summary>
/// <param name="Color">#rrggbb</param>
public sealed record CalendarCategory(string Name, string Color)
{
    public bool Matches(string category) => string.Equals(category.Trim(), Name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The categories offered for events (Einstellungen → Kalender), kept apart from the tags of mails. Until the user
/// changes them, a short list of what is often needed.
/// </summary>
public sealed class CalendarCategoryService(ISettingsStore settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public event EventHandler? Changed;

    // Colours of the choice in the settings that the calendars do not get by default, so a line stands out from the entry.
    public static IReadOnlyList<CalendarCategory> Defaults =>
    [
        new(T("Besprechung"), "#00B7C3"),
        new(T("Kunde"), "#E3008C"),
        new(T("Anruf"), "#8CBD18"),
        new(T("Projekt"), "#5C2E91"),
        new(T("Reise"), "#C19C00"),
        new(T("Ferien"), "#498205"),
        new(T("Privat"), "#69797E"),
        new(T("Nachfassen"), "#D13438"),
    ];

    public async Task<IReadOnlyList<CalendarCategory>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await settings.GetAsync(SettingKeys.CalendarCategories, cancellationToken) is { } json
                ? JsonSerializer.Deserialize<List<CalendarCategory>>(json, Json) ?? [.. Defaults]
                : [.. Defaults];
        }
        catch (JsonException)
        {
            return [.. Defaults];
        }
    }

    public async Task SaveAsync(IReadOnlyList<CalendarCategory> categories, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categories);
        await settings.SetAsync(SettingKeys.CalendarCategories, JsonSerializer.Serialize(categories, Json), cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The known categories among an event's, in the order of the list (unknown ones have no colour and are left out).</summary>
    public static IReadOnlyList<CalendarCategory> Of(IEnumerable<string> categories, IReadOnlyList<CalendarCategory> known)
    {
        var names = categories.ToList();
        return [.. known.Where(k => names.Any(k.Matches))];
    }
}
