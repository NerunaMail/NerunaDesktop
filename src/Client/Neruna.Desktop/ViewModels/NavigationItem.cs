using CommunityToolkit.Mvvm.ComponentModel;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>A button in the upper part of the navigation rail; order and visibility are chosen in Einstellungen → Design.</summary>
internal sealed partial class NavigationItem(Section section, string label, string iconKey) : ObservableObject
{
    public Section Section { get; } = section;

    public string Label { get; } = label;

    /// <summary>Key of the icon geometry in App.axaml.</summary>
    public string IconKey { get; } = iconKey;

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>The page is the one in front.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>A count on the button (unread chat messages), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial string? Badge { get; set; }

    public bool HasBadge => Badge is not null;

    public static IEnumerable<NavigationItem> Defaults() =>
    [
        new(Section.Mail, T("E-Mail"), "IconMail"),
        new(Section.Calendar, T("Kalender"), "IconCalendar"),
        new(Section.Contacts, T("Kontakte"), "IconPeople"),
        new(Section.Chat, "Chat", "IconChat"),
    ];
}
