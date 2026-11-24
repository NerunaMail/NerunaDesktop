using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Neruna.Core.Calendar;

namespace Neruna.Desktop.Controls;

/// <summary>
/// A time of day to type: "930", "9:30", "14" … – taken over with Enter, Tab or leaving the field. ↑/↓ move by
/// <see cref="MinuteIncrement"/> minutes, Page ↑/↓ by an hour. The text is selected when the field gets the focus, so
/// typing replaces it. Something that is no time goes back to the time before.
/// </summary>
internal sealed class TimeBox : TextBox
{
    public static readonly StyledProperty<TimeSpan?> SelectedTimeProperty =
        AvaloniaProperty.Register<TimeBox, TimeSpan?>(nameof(SelectedTime), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> MinuteIncrementProperty = AvaloniaProperty.Register<TimeBox, int>(nameof(MinuteIncrement), 15);

    public TimeBox()
    {
        Width = 76;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        PlaceholderText = "hh:mm";
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    public TimeSpan? SelectedTime
    {
        get => GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    public int MinuteIncrement
    {
        get => GetValue(MinuteIncrementProperty);
        set => SetValue(MinuteIncrementProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedTimeProperty)
        {
            Show();
        }
    }

    private void Show() => Text = SelectedTime is { } time ? TimeInput.Format(time) : string.Empty;

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        SelectAll();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        Commit();
        base.OnLostFocus(e);
    }

    private void Commit()
    {
        if (TimeInput.Parse(Text) is { } time)
        {
            SelectedTime = time;
        }

        Show();
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                SelectAll();
                break; // not handled: Enter still saves the dialog (default button)
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown:
                var current = TimeInput.Parse(Text) ?? SelectedTime ?? new TimeSpan(9, 0, 0);
                var step = e.Key is Key.PageUp or Key.PageDown ? 60 : Math.Max(1, MinuteIncrement);
                SelectedTime = TimeInput.Step(current, step, e.Key is Key.Up or Key.PageUp);
                Show();
                SelectAll();
                e.Handled = true;
                break;
        }
    }
}
