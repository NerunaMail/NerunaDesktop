using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Graph;

/// <summary>
/// Colours on the server (when the account stores them there): the calendar's colour – one of Outlook's few – and the
/// mailbox's category list ("masterCategories", 25 colours), which Outlook everywhere shows.
/// </summary>
internal sealed partial class GraphCalendarProvider : ICalendarColors, ICalendarCategoryColors
{
    public async Task SetCalendarColorAsync(CalendarInfo calendar, string color, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (GraphTasks.IsTaskList(calendar))
        {
            throw new NotSupportedException(T("Aufgabenlisten haben bei Microsoft keine Farbe."));
        }

        await graph.SendJsonAsync(HttpMethod.Patch, $"me/calendars/{calendar.RemoteId}",
            new Dictionary<string, object> { ["color"] = OutlookColors.NearestCalendar(color) }, cancellationToken);
    }

    public async Task<IReadOnlyList<CalendarCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCategoriesGrantedAsync(cancellationToken);
        return [.. (await graph.GetAllAsync("me/outlook/masterCategories", cancellationToken))
            .Where(c => c.Str("displayName") is { Length: > 0 })
            .Select(c => new CalendarCategory(c.Str("displayName")!, PresetColor(c.Str("color")) ?? OutlookColors.Categories[12]))];
    }

    public async Task SetCategoryColorAsync(string name, string color, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await EnsureCategoriesGrantedAsync(cancellationToken);
        var preset = "preset" + OutlookColors.NearestCategory(color).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var existing = (await graph.GetAllAsync("me/outlook/masterCategories", cancellationToken))
            .FirstOrDefault(c => string.Equals(c.Str("displayName"), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing.Str("id") is { } id)
        {
            if (existing.Str("color") != preset)
            {
                await graph.SendJsonAsync(HttpMethod.Patch, $"me/outlook/masterCategories/{id}", new Dictionary<string, object> { ["color"] = preset }, cancellationToken);
            }
        }
        else
        {
            await graph.SendJsonAsync(HttpMethod.Post, "me/outlook/masterCategories",
                new Dictionary<string, object> { ["displayName"] = name.Trim(), ["color"] = preset }, cancellationToken);
        }
    }

    // "preset7" → the colour; "none" → null.
    private static string? PresetColor(string? preset) =>
        preset is { Length: > 6 } && preset.StartsWith("preset", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(preset.AsSpan(6), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index)
            ? OutlookColors.CategoryColor(index)
            : null;

    private async Task EnsureCategoriesGrantedAsync(CancellationToken cancellationToken)
    {
        if (!await graph.GrantsAsync(MicrosoftAccount.MailboxSettingsScope, cancellationToken))
        {
            throw new NotSupportedException(T("Für die Kategorien des Postfachs braucht Neruna eine zusätzliche Berechtigung. Bitte einmal neu bei Microsoft anmelden: Einstellungen → Konten → Bearbeiten → «Erneut bei Microsoft anmelden»."));
        }
    }
}
