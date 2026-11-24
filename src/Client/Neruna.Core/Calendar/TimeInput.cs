namespace Neruna.Core.Calendar;

/// <summary>
/// A time of day as typed on the keyboard: "9" (9:00), "930" or "0930" (9:30), "9:30", "9.30", "9,30", "14h", "1430".
/// </summary>
public static class TimeInput
{
    /// <returns>The time, or null when the text is no time of day.</returns>
    public static TimeSpan? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim().TrimEnd('h', 'H').Trim();
        int hours;
        int minutes;
        var separator = value.IndexOfAny([':', '.', ',', ' ']);
        if (separator >= 0)
        {
            var minutePart = value[(separator + 1)..].Trim();
            if (!int.TryParse(value[..separator], out hours) || !int.TryParse(minutePart.Length == 0 ? "0" : minutePart, out minutes)
                || minutePart.Length > 2)
            {
                return null;
            }

            // "9:3" means 9:30, as on a clock.
            if (minutePart.Length == 1)
            {
                minutes *= 10;
            }
        }
        else
        {
            if (value.Length is 0 or > 4 || !value.All(char.IsAsciiDigit))
            {
                return null;
            }

            (hours, minutes) = value.Length <= 2
                ? (int.Parse(value, System.Globalization.CultureInfo.InvariantCulture), 0)
                : (int.Parse(value[..^2], System.Globalization.CultureInfo.InvariantCulture), int.Parse(value[^2..], System.Globalization.CultureInfo.InvariantCulture));
        }

        return hours is >= 0 and <= 23 && minutes is >= 0 and <= 59 ? new TimeSpan(hours, minutes, 0) : hours == 24 && minutes == 0 ? TimeSpan.Zero : null;
    }

    /// <summary>"09:30".</summary>
    public static string Format(TimeSpan time) => $"{time.Hours:00}:{time.Minutes:00}";

    /// <summary>
    /// A step up or down (arrow keys) onto the next multiple of <paramref name="step"/> minutes, around midnight.
    /// </summary>
    public static TimeSpan Step(TimeSpan time, int step, bool up)
    {
        var minutes = (int)time.TotalMinutes;
        var next = up ? (minutes / step + 1) * step : (minutes % step == 0 ? minutes - step : minutes / step * step);
        next = ((next % 1440) + 1440) % 1440;
        return TimeSpan.FromMinutes(next);
    }
}
