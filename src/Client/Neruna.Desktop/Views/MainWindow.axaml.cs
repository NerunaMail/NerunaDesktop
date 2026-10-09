using Avalonia.Controls;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class MainWindow : Window
{
    private bool _mayClose;

    private bool _restartForUpdate;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // "Jetzt neu starten": close as usual (drafts are asked about), then install and start again.
                Activated += (_, _) => vm.UpdateChatActive();
                Deactivated += (_, _) => vm.UpdateChatActive();
                PropertyChanged += (_, e) =>
                {
                    if (e.Property == WindowStateProperty)
                    {
                        vm.UpdateChatActive();
                    }
                };
                vm.SettingsPage.Updates.RestartRequested += (_, _) =>
                {
                    _restartForUpdate = true;
                    Close();
                };
            }
        };
        Closed += (_, _) =>
        {
            if (_restartForUpdate && DataContext is MainWindowViewModel vm)
            {
                vm.SettingsPage.Updates.ApplyAndRestart();
            }
        };
    }

    // A status picked in the header: close the list.
    private void OnPresencePicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => PresenceButton.Flyout?.Hide();

    // Open drafts with unsaved changes: ask first (Speichern / Nicht speichern / Abbrechen).
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_mayClose || e.Cancel || DataContext is not MainWindowViewModel vm || vm.MailPage.UnsavedDrafts.Count == 0)
        {
            return;
        }

        e.Cancel = true;
        if (await vm.PrepareCloseAsync(AskAsync))
        {
            _mayClose = true;
            Close();
        }
        else
        {
            _restartForUpdate = false;
        }
    }

    private async Task<CloseChoice> AskAsync(IReadOnlyList<string> titles)
    {
        var message = titles.Count == 1
            ? $"Der Entwurf «{titles[0]}» hat ungespeicherte Änderungen. Vor dem Beenden speichern?"
            : $"{titles.Count} Entwürfe haben ungespeicherte Änderungen: {string.Join(", ", titles.Select(t => "«" + t + "»"))}. Vor dem Beenden speichern?";
        return await ChoiceDialog.ShowAsync(this, "Neruna beenden", message,
            ("Speichern", CloseChoice.Save, true), ("Nicht speichern", CloseChoice.Discard, false), ("Abbrechen", CloseChoice.Cancel, false));
    }
}
