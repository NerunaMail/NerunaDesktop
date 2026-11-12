using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Neruna.Desktop.Views;

internal sealed partial class AccountsView : UserControl
{
    /// <summary>The extra calendars (Kalender → Zusatzkalender) instead of the accounts.</summary>
    public static readonly Avalonia.StyledProperty<bool> ExtrasProperty = Avalonia.AvaloniaProperty.Register<AccountsView, bool>(nameof(Extras));

    public AccountsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ShowList();
    }

    public bool Extras
    {
        get => GetValue(ExtrasProperty);
        set => SetValue(ExtrasProperty, value);
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ExtrasProperty)
        {
            ShowList();
        }
    }

    private void ShowList() =>
        Cards.ItemsSource = DataContext is ViewModels.AccountsViewModel vm ? (Extras ? vm.ExtraCalendars : vm.MainItems) : null;

    // A color chosen: which one is taken first – closing the picker detaches the button from its color – then the
    // picker closes and the color is set.
    private void OnColorPicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var choice = (sender as Control)?.DataContext as ViewModels.AccountColorChoice;
        if ((sender as Avalonia.Visual)?.FindLogicalAncestorOfType<Avalonia.Controls.Primitives.Popup>() is { } popup)
        {
            popup.IsOpen = false;
        }

        if (choice is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => choice.ChooseCommand.Execute(null));
        }
    }
}
