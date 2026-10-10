using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// A calendar or task list in a side list (Kalender, Aufgaben): tick, colour, own name, "Aufgaben im Kalender anzeigen"
/// and "nur diese anzeigen". Colour, name and the tasks switch are one setting per list – the same menu edits them in
/// both places (<see cref="Views.CollectionAppearanceEditor"/>).
/// </summary>
internal abstract partial class CollectionListItem(CalendarInfo info, string color) : ObservableObject
{
    public CalendarInfo Info { get; } = info;

    public string Key { get; } = CalendarController.CalendarKey(info);

    public string Color { get; } = color;

    public IBrush Brush { get; } = Avalonia.Media.Brush.Parse(color);

    public string Name => Info.Name;

    /// <summary>The name being edited in the colour/name menu.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; } = info.Name;

    /// <summary>"Auf dem Server: Personal" when the list has an own name.</summary>
    public string? ServerNameText => Info.ServerName is { } server ? T("Auf dem Server: ") + server : null;

    public bool IsRenamed => Info.ServerName is not null;

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>The server keeps tasks in it: they can be shown in the calendar or only under "Aufgaben".</summary>
    public bool HasTasks => Info.HasTasks;

    /// <summary>"Aufgaben im Kalender anzeigen" (default off).</summary>
    [ObservableProperty]
    public partial bool ShowTasks { get; set; }

    /// <summary>Only this one is shown right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoloTip))]
    public partial bool IsSolo { get; set; }

    /// <summary>Another one is shown alone: this one is greyed out (its tick stays).</summary>
    [ObservableProperty]
    public partial bool IsDimmed { get; set; }

    public abstract string SoloTip { get; }
}

/// <summary>The page that owns the list items: applies a colour or name chosen in the menu.</summary>
internal interface ICollectionListHost
{
    Task SetColorAsync(CollectionListItem item, string? color);

    Task RenameAsync(CollectionListItem item, string? name);
}

/// <summary>
/// "Nur diese anzeigen": a temporary filter on top of the ticks, which stay as they are – one more click brings back
/// exactly the lists ticked before. Not saved: after a restart the ticks count again.
/// </summary>
internal sealed class SoloFilter
{
    public string? Key { get; private set; }

    public bool IsActive => Key is not null;

    /// <summary>Shows only <paramref name="item"/>, or – if it already is the one – all ticked ones again.</summary>
    public void Toggle(CollectionListItem item, IEnumerable<CollectionListItem> all)
    {
        ArgumentNullException.ThrowIfNull(item);
        Set(Key == item.Key ? null : item.Key, all);
    }

    public void Set(string? key, IEnumerable<CollectionListItem> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        Key = key;
        foreach (var item in all)
        {
            Apply(item);
        }
    }

    /// <summary>Marks a (new) item as the one shown alone or as greyed out.</summary>
    public void Apply(CollectionListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.IsSolo = Key == item.Key;
        item.IsDimmed = Key is not null && Key != item.Key;
    }

    /// <summary>Whether the list is shown: the one alone, otherwise as ticked.</summary>
    public bool Shows(CalendarInfo list, bool ticked) => Key is null ? ticked : CalendarController.CalendarKey(list) == Key;
}
