using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Mail;

/// <summary>How far back an account keeps its mail complete on the device (content and attachments), for offline use.</summary>
public enum OfflinePeriod
{
    /// <summary>Only what was opened (the content is kept from then on).</summary>
    OpenedOnly,
    Week,
    Month,
    ThreeMonths,
    SixMonths,
    Year,
    All,
}

public static class OfflinePeriods
{
    /// <summary>Default for accounts without a choice.</summary>
    public const OfflinePeriod Default = OfflinePeriod.Month;

    public static IReadOnlyList<OfflinePeriod> All { get; } = Enum.GetValues<OfflinePeriod>();

    public static string Key(Guid accountId) => $"mail.offline.{accountId:N}";

    public static string Label(OfflinePeriod period) => period switch
    {
        OfflinePeriod.OpenedOnly => T("Nur geöffnete Mails"),
        OfflinePeriod.Week => T("1 Woche"),
        OfflinePeriod.Month => T("1 Monat"),
        OfflinePeriod.ThreeMonths => T("3 Monate"),
        OfflinePeriod.SixMonths => T("6 Monate"),
        OfflinePeriod.Year => T("1 Jahr"),
        _ => T("Alle Mails"),
    };

    /// <summary>The oldest date kept complete; null for all (and for only opened ones: nothing is fetched ahead).</summary>
    public static DateTimeOffset? Since(OfflinePeriod period, DateTimeOffset now) => period switch
    {
        OfflinePeriod.Week => now.AddDays(-7),
        OfflinePeriod.Month => now.AddMonths(-1),
        OfflinePeriod.ThreeMonths => now.AddMonths(-3),
        OfflinePeriod.SixMonths => now.AddMonths(-6),
        OfflinePeriod.Year => now.AddYears(-1),
        _ => null,
    };
}
