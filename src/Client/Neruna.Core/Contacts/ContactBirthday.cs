using System.Globalization;

namespace Neruna.Core.Contacts;

/// <summary>A birthday (vCard BDAY); the year may be unknown (vCard 4 "--MMDD").</summary>
public sealed record ContactBirthday(int? Year, int Month, int Day)
{
    /// <summary>Reads BDAY values: 19850415, 1985-04-15, 1985-04-15T00:00:00Z, --0415, --04-15.</summary>
    public static ContactBirthday? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var time = text.IndexOf('T', StringComparison.Ordinal);
        if (time > 0)
        {
            text = text[..time];
        }

        int? year = null;
        if (text.StartsWith("--", StringComparison.Ordinal))
        {
            text = text[2..];
        }
        else
        {
            var digits = text.Replace("-", string.Empty, StringComparison.Ordinal);
            if (digits.Length != 8 || !int.TryParse(digits.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y))
            {
                return null;
            }

            year = y;
            text = digits[4..];
        }

        text = text.Replace("-", string.Empty, StringComparison.Ordinal);
        return text.Length == 4
               && int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
               && int.TryParse(text.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var day)
               && IsValid(year, month, day)
            ? new ContactBirthday(year is < 1800 ? null : year, month, day) // 1604 etc.: placeholder years of some apps
            : null;
    }

    /// <summary>What the user typed: day, month and optionally the year, separated by . / - or space.</summary>
    public static ContactBirthday? ParseInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split(['.', '/', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month))
        {
            return null;
        }

        int? year = null;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var y) || y is < 1800 or > 2200)
            {
                return null;
            }

            year = y;
        }

        return IsValid(year, month, day) ? new ContactBirthday(year, month, day) : null;
    }

    /// <summary>For the editor: 15.04.1985 / 15.04. (with the culture's date separator).</summary>
    public string ToInput(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var separator = culture.DateTimeFormat.DateSeparator;
        return Year is { } year ? $"{Day:00}{separator}{Month:00}{separator}{year:0000}" : $"{Day:00}{separator}{Month:00}{separator}";
    }

    /// <summary>The BDAY value: 1985-04-15 (vCard 3) or 19850415 / --0415 (vCard 4).</summary>
    public string ToVCardValue(bool v4) =>
        Year is { } year
            ? v4 ? $"{year:0000}{Month:00}{Day:00}" : $"{year:0000}-{Month:00}-{Day:00}"
            : v4 ? $"--{Month:00}{Day:00}" : $"--{Month:00}-{Day:00}";

    /// <summary>The date in <paramref name="year"/>; 29 February becomes the 28th in other years.</summary>
    public DateOnly In(int year) => new(year, Month, Month == 2 && Day == 29 && !DateTime.IsLeapYear(year) ? 28 : Day);

    private static bool IsValid(int? year, int month, int day) =>
        month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year ?? 2000, month); // 2000: leap year, so 29.2. is fine without a year
}
