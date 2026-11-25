using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>Monthly or yearly: on the day of the month, or on the n-th (last) weekday.</summary>
internal sealed record MonthlyChoice(int? Position, DayOfWeek Day, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Wiederholung" of events and tasks: how often (daily … yearly, every n), on which weekdays, on which day of the
/// month, and until when (never, a day, a number of times). A rule from elsewhere that holds more than this is shown as
/// "Benutzerdefiniert (unverändert)" and kept as it is.
/// </summary>
internal sealed partial class RecurrenceEditorViewModel : ObservableObject
{
    private readonly Func<DateTime> _start;
    private readonly RecurrenceSpec? _read;
    private readonly DateTime? _readUntil;

    /// <param name="start">The date the series starts on (changes with the editor's start date).</param>
    public RecurrenceEditorViewModel(RecurrenceKind kind, RecurrenceSpec? spec, DateTime? until, Func<DateTime> start)
    {
        _start = start;
        _read = spec;
        _readUntil = until;
        Options = kind == RecurrenceKind.Custom ? RecurrenceOption.All : [.. RecurrenceOption.All.Where(o => o.Kind != RecurrenceKind.Custom)];
        foreach (var day in Enumerable.Range(0, 7).Select(i => (DayOfWeek)((i + 1) % 7)))
        {
            var choice = new WeekdayChoice(day) { IsChecked = spec?.Frequency == RecurrenceFrequency.Weekly && spec.Weekdays.Contains(day) };
            Weekdays.Add(choice);
        }

        Interval = spec?.Interval ?? 1;
        EndMode = spec?.Count is not null ? 2 : until is not null ? 1 : 0;
        EndDate = until ?? start().Date.AddMonths(3);
        Count = spec?.Count ?? 10;
        Selected = Options.First(o => o.Kind == kind);
        RefreshMonthly(spec);
    }

    public IReadOnlyList<RecurrenceOption> Options { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails), nameof(IsWeekly), nameof(HasMonthly), nameof(UnitText), nameof(IsCustom))]
    public partial RecurrenceOption Selected { get; set; }

    partial void OnSelectedChanged(RecurrenceOption value)
    {
        // A weekly series starts on the start's weekday.
        if (value.Kind == RecurrenceKind.Weekly && Weekdays.All(w => !w.IsChecked))
        {
            Weekdays.First(w => w.Day == _start().DayOfWeek).IsChecked = true;
        }

        RefreshMonthly(null);
    }

    public RecurrenceKind Kind => Selected.Kind;

    public bool HasDetails => Selected.Kind is not (RecurrenceKind.None or RecurrenceKind.Custom);

    public bool IsCustom => Selected.Kind == RecurrenceKind.Custom;

    public bool IsWeekly => Selected.Kind == RecurrenceKind.Weekly;

    public bool HasMonthly => Selected.Kind is RecurrenceKind.Monthly or RecurrenceKind.Yearly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitText))]
    public partial decimal? Interval { get; set; }

    public string UnitText => (Selected.Kind, (int)(Interval ?? 1) == 1) switch
    {
        (RecurrenceKind.Daily, true) => T("Tag"),
        (RecurrenceKind.Daily, false) => T("Tage"),
        (RecurrenceKind.Weekly, true) => T("Woche"),
        (RecurrenceKind.Weekly, false) => T("Wochen"),
        (RecurrenceKind.Monthly, true) => T("Monat"),
        (RecurrenceKind.Monthly, false) => T("Monate"),
        (_, true) => T("Jahr"),
        _ => T("Jahre"),
    };

    public ObservableCollection<WeekdayChoice> Weekdays { get; } = [];

    public ObservableCollection<MonthlyChoice> MonthlyChoices { get; } = [];

    [ObservableProperty]
    public partial MonthlyChoice? Monthly { get; set; }

    /// <summary>0: no end, 1: on <see cref="EndDate"/>, 2: after <see cref="Count"/> times.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndsOnDate), nameof(EndsAfterCount))]
    public partial int EndMode { get; set; }

    public bool EndsOnDate => EndMode == 1;

    public bool EndsAfterCount => EndMode == 2;

    public IReadOnlyList<string> EndModes { get; } = [T("ohne Ende"), T("endet am"), T("endet nach")];

    [ObservableProperty]
    public partial DateTime? EndDate { get; set; }

    [ObservableProperty]
    public partial decimal? Count { get; set; }

    /// <summary>The start date changed: the choices "am 14." / "am zweiten Dienstag" follow it.</summary>
    public void StartChanged() => RefreshMonthly(null);

    private void RefreshMonthly(RecurrenceSpec? keep)
    {
        var start = _start().Date;
        var previous = keep is { Position: { } p } && keep.Weekdays.Count > 0 ? (int?)p : Monthly?.Position;
        var yearly = Selected.Kind == RecurrenceKind.Yearly;
        MonthlyChoice Choice(int? position, DayOfWeek day) => new(position, day, RecurrenceSpec.OnDay(yearly, position, day, start));
        MonthlyChoices.Clear();
        MonthlyChoices.Add(Choice(null, start.DayOfWeek));
        var position = RecurrenceSpec.PositionOf(start);
        if (position <= 4)
        {
            MonthlyChoices.Add(Choice(position, start.DayOfWeek));
        }

        if (RecurrenceSpec.IsLastOfMonth(start))
        {
            MonthlyChoices.Add(Choice(-1, start.DayOfWeek));
        }

        // A rule read from the server on another weekday than the start's stays choosable.
        if (keep is { Position: { } kept } && keep.Weekdays.Count > 0 && MonthlyChoices.All(c => c.Position != kept || c.Day != keep.Weekdays[0]))
        {
            MonthlyChoices.Add(Choice(kept, keep.Weekdays[0]));
        }

        Monthly = keep is { Position: { } k } && keep.Weekdays.Count > 0
            ? MonthlyChoices.First(c => c.Position == k && c.Day == keep.Weekdays[0])
            : MonthlyChoices.FirstOrDefault(c => c.Position == previous) ?? MonthlyChoices[0];
    }

    /// <summary>What to save, or an error to show.</summary>
    public (RecurrenceKind Kind, RecurrenceSpec? Spec, DateTime? Until, string? Error) Build()
    {
        var until = HasDetails && EndMode == 1 && EndDate is { } day ? day.Date : (DateTime?)null;
        if (until is { } last && last < _start().Date)
        {
            return (Kind, null, null, T("Die Serie kann nicht vor dem ersten Termin enden."));
        }

        if (Selected.Kind == RecurrenceKind.None)
        {
            return (RecurrenceKind.None, null, null, null);
        }

        if (Selected.Kind == RecurrenceKind.Custom)
        {
            return (RecurrenceKind.Custom, _read, _readUntil, null);
        }

        var frequency = RecurrenceSpec.FrequencyOf(Selected.Kind)!.Value;
        var spec = new RecurrenceSpec(frequency, Math.Clamp((int)(Interval ?? 1), 1, 999))
        {
            Count = EndMode == 2 ? Math.Clamp((int)(Count ?? 1), 1, 999) : null,
        };
        if (frequency == RecurrenceFrequency.Weekly)
        {
            var days = Weekdays.Where(w => w.IsChecked).Select(w => w.Day).ToList();
            spec = spec with { Weekdays = days.Count > 0 ? days : [_start().DayOfWeek] };
        }
        else if (frequency is RecurrenceFrequency.Monthly or RecurrenceFrequency.Yearly && Monthly is { Position: { } position } choice)
        {
            spec = spec with { Position = position, Weekdays = [choice.Day] };
        }

        return (Selected.Kind, spec, until, null);
    }
}
