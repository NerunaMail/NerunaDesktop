using Avalonia.Controls;
using Avalonia.Interactivity;
using Neruna.Desktop.ViewModels;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

internal sealed partial class CloudView : UserControl
{
    public CloudView() => InitializeComponent();

    // "Trennen": a new code from the portal is needed to connect again – so ask first.
    private async void OnDisconnect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CloudViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var sure = await ChoiceDialog.ShowAsync(owner, T("Cloud-Verbindung trennen"),
            T("Dieses Gerät wird von der Neruna Cloud getrennt. Für eine neue Verbindung braucht es einen neuen Verbindungscode aus dem Portal."),
            (T("Trennen"), true, false), (T("Abbrechen"), false, true));
        if (sure)
        {
            await vm.DisconnectCommand.ExecuteAsync(null);
        }
    }
}
