using System.Globalization;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Mail;

/// <summary>
/// An out-of-office series ("jeden Freitag") on a server that only knows one period (Exchange, Microsoft 365): the
/// server gets the next of the series' days as its period; once that has passed, Neruna sets the following one – at
/// its next start or check. Kept per account on this device.
/// </summary>
public static class AutoReplySeries
{
    private static string Key(Guid connectionId) => $"autoreply.series.{connectionId:N}";

    public static async Task<IReadOnlyList<DayOfWeek>?> GetAsync(ISettingsStore settings, Guid connectionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (await settings.GetAsync(Key(connectionId), cancellationToken) is not { Length: > 0 } text)
        {
            return null;
        }

        var days = text.Split(',').Select(d => int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 6 ? (DayOfWeek?)n : null)
            .OfType<DayOfWeek>().Distinct().ToList();
        return days.Count > 0 ? days : null;
    }

    public static Task SetAsync(ISettingsStore settings, Guid connectionId, IReadOnlyList<DayOfWeek>? days, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.SetAsync(Key(connectionId),
            days is { Count: > 0 } ? string.Join(',', days.Select(d => ((int)d).ToString(CultureInfo.InvariantCulture))) : null, cancellationToken);
    }

    /// <summary>
    /// The period for the server: today if today is one of the days, else the next such day – with the days right after
    /// it that belong to the series too (Thursday and Friday: one period). Whole local days; the end is exclusive.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) NextPeriod(IReadOnlyCollection<DayOfWeek> days, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(days);
        if (days.Count == 0)
        {
            throw new ArgumentException("A series needs at least one day.", nameof(days));
        }

        var today = now.ToLocalTime().Date;
        var first = Enumerable.Range(0, 7).Select(i => today.AddDays(i)).First(d => days.Contains(d.DayOfWeek));
        var last = first;
        while (days.Count < 7 && days.Contains(last.AddDays(1).DayOfWeek))
        {
            last = last.AddDays(1);
        }

        if (days.Count == 7)
        {
            last = first.AddDays(6);
        }

        return (Local(first), Local(last.AddDays(1)));
    }

    private static DateTimeOffset Local(DateTime day) => new(day, TimeZoneInfo.Local.GetUtcOffset(day));

    /// <summary>"Jeden Freitag", "Jeden Montag, Freitag" – the days from Monday on.</summary>
    public static string Describe(IReadOnlyCollection<DayOfWeek> days, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(days);
        culture ??= Culture;
        var names = Week.Where(days.Contains).Select(d => culture.DateTimeFormat.GetDayName(d)).ToList();
        return days.Count == 7 ? T("Jeden Tag") : F("Jeden {0}", string.Join(", ", names));
    }

    /// <summary>Monday to Sunday, the order the days are shown in.</summary>
    public static IReadOnlyList<DayOfWeek> Week { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
}
