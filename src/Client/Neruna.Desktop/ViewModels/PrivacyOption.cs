using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>"Normal", "Privat", "Vertraulich" (iCalendar CLASS) for events and tasks.</summary>
internal sealed record PrivacyOption(EventPrivacy Value, string Label)
{
    public static IReadOnlyList<PrivacyOption> All { get; } =
    [
        new(EventPrivacy.Public, T("Normal")),
        new(EventPrivacy.Private, T("Privat")),
        new(EventPrivacy.Confidential, T("Vertraulich")),
    ];

    public static PrivacyOption Of(EventPrivacy value) => All.First(o => o.Value == value);

    public override string ToString() => Label;
}
