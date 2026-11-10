using System.Text.Json;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Calendar;

/// <summary>
/// A category of events ("Besprechung", "Kunde" …): name and colour. On the server only the name is stored – iCalendar
/// CATEGORIES (CalDAV), the categories of Microsoft 365 and Exchange – so other programs see it and Neruna matches by name.
/// </summary>
/// <param name="Color">#rrggbb</param>
/// <param name="Source">The organisation it comes from (Neruna Cloud, not editable here); null: the user's own.</param>
public sealed record CalendarCategory(string Name, string Color, string? Source = null)
{
    public bool IsFromCloud => Source is not null;

    public bool Matches(string category) => string.Equals(category.Trim(), Name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The categories offered for events (Einstellungen → Kalender), kept apart from the tags of mails: the organisations'
/// (Neruna Cloud → Schlagwörter &amp; Kategorien) first, then the user's own. Until the user changes them, the own ones
/// are a short list of what is often needed.
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

    /// <summary>The organisations' first (an own one of the same name gives way), then the own ones.</summary>
    public async Task<IReadOnlyList<CalendarCategory>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var cloud = (await GetCloudAsync(cancellationToken)).Values.SelectMany(c => c).DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var own = await GetOwnAsync(cancellationToken);
        return [.. cloud, .. own.Where(o => !cloud.Any(c => c.Matches(o.Name)))];
    }

    public async Task<IReadOnlyList<CalendarCategory>> GetOwnAsync(CancellationToken cancellationToken = default) =>
        Read<List<CalendarCategory>>(await settings.GetAsync(SettingKeys.CalendarCategories, cancellationToken))?.Select(c => c with { Source = null }).ToList()
        ?? [.. Defaults];

    /// <summary>The user's own categories, as listed (those of an organisation are left out).</summary>
    public async Task SaveAsync(IReadOnlyList<CalendarCategory> categories, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categories);
        await settings.SetAsync(SettingKeys.CalendarCategories,
            JsonSerializer.Serialize(categories.Where(c => !c.IsFromCloud).Select(c => new { c.Name, c.Color }), Json), cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The categories of one organisation (replacing what it had); empty removes them.</summary>
    /// <returns>Something changed.</returns>
    public async Task<bool> SetCloudAsync(string source, IReadOnlyList<CalendarCategory> categories, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var all = await GetCloudAsync(cancellationToken);
        var offered = categories.Select(c => c with { Source = source }).ToList();
        if (all.TryGetValue(source, out var current) ? current.SequenceEqual(offered) : offered.Count == 0)
        {
            return false;
        }

        if (offered.Count == 0)
        {
            all.Remove(source);
        }
        else
        {
            all[source] = offered;
        }

        await settings.SetAsync(SettingKeys.CalendarCategoriesCloud, all.Count == 0 ? null : JsonSerializer.Serialize(all, Json), cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<IReadOnlyCollection<string>> GetCloudSourcesAsync(CancellationToken cancellationToken = default) =>
        (await GetCloudAsync(cancellationToken)).Keys;

    /// <summary>The known categories among an event's, in the order of the list (unknown ones have no colour and are left out).</summary>
    public static IReadOnlyList<CalendarCategory> Of(IEnumerable<string> categories, IReadOnlyList<CalendarCategory> known)
    {
        var names = categories.ToList();
        return [.. known.Where(k => names.Any(k.Matches))];
    }

    private async Task<Dictionary<string, List<CalendarCategory>>> GetCloudAsync(CancellationToken cancellationToken) =>
        Read<Dictionary<string, List<CalendarCategory>>>(await settings.GetAsync(SettingKeys.CalendarCategoriesCloud, cancellationToken)) ?? [];

    private static TValue? Read<TValue>(string? json) where TValue : class
    {
        try
        {
            return json is null ? null : JsonSerializer.Deserialize<TValue>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
