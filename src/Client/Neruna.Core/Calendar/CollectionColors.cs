using Neruna.Core.Contacts;

namespace Neruna.Core.Calendar;

/// <summary>
/// The colour a calendar or address book is shown in: one chosen in Neruna (kept on this device, the server keeps its
/// own), else the server's, else one of the palette – the same on the desktop and the phone.
/// </summary>
public static class CollectionColors
{
    /// <summary>The palette for collections without a colour, and the colours offered to choose from.</summary>
    public static IReadOnlyList<string> Palette { get; } =
        ["#0F6CBD", "#C239B3", "#0B6A0B", "#CA5010", "#8764B8", "#038387", "#C50F1F", "#986F0B", "#4F6BED", "#69797E"];

    public static string Key(CalendarInfo calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return $"calendar.color.{calendar.ConnectionId:N}.{calendar.RemoteId}";
    }

    public static string Key(AddressBookInfo book)
    {
        ArgumentNullException.ThrowIfNull(book);
        return $"contacts.color.{book.ConnectionId:N}.{book.RemoteId}";
    }

    /// <summary>The colours of the calendars, in their order (palette colours handed out in that order).</summary>
    public static async Task<Dictionary<(Guid, string), string>> ResolveAsync(ISettingsStore settings, IReadOnlyList<CalendarInfo> calendars, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(calendars);
        var result = new Dictionary<(Guid, string), string>();
        var index = 0;
        foreach (var info in calendars)
        {
            result[(info.ConnectionId, info.RemoteId)] = await settings.GetAsync(Key(info), cancellationToken) ?? info.Color ?? Palette[index++ % 6];
        }

        return result;
    }

    /// <summary>The calendars with the colour they are shown in (chosen, the server's or a palette colour) in <see cref="CalendarInfo.Color"/>.</summary>
    public static async Task<IReadOnlyList<CalendarInfo>> WithColorsAsync(ISettingsStore settings, IReadOnlyList<CalendarInfo> calendars, CancellationToken cancellationToken = default)
    {
        var colors = await ResolveAsync(settings, calendars, cancellationToken);
        return [.. calendars.Select(c => c with { Color = colors[(c.ConnectionId, c.RemoteId)] })];
    }
}
