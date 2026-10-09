using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Neruna.Desktop.ViewModels;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

/// <summary>Desktop notification bottom right; closes after a few seconds unless the pointer rests on it.</summary>
internal sealed partial class NotificationWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);
    private readonly DispatcherTimer _timer = new() { Interval = Lifetime };

    public NotificationWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Close();
        Opened += (_, _) => _timer.Start();
        PointerEntered += (_, _) => _timer.Stop();
        PointerExited += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        this.FindControl<Button>("CloseButton")!.Click += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Handled || DataContext is not NotificationViewModel notification)
        {
            return;
        }

        e.Handled = true;
        Close();
        await notification.Open();
    }
}
