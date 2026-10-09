using Avalonia.Controls;
using Neruna.Desktop.ViewModels;
using static Neruna.Core.Localization.Texts;

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

    // A status picked in the header: set it, then close the list.
    private void OnPresencePicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PresenceChoice choice } && DataContext is MainWindowViewModel vm)
        {
            vm.ChatPage.SetPresenceCommand.Execute(choice.Key);
        }

        PresenceButton.Flyout?.Hide();
    }

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
            ? F("Der Entwurf «{0}» hat ungespeicherte Änderungen. Vor dem Beenden speichern?", titles[0])
            : F("{0} Entwürfe haben ungespeicherte Änderungen: {1}. Vor dem Beenden speichern?", titles.Count, string.Join(", ", titles.Select(t => "«" + t + "»")));
        return await ChoiceDialog.ShowAsync(this, T("Neruna beenden"), message,
            (T("Speichern"), CloseChoice.Save, true), (T("Nicht speichern"), CloseChoice.Discard, false), (T("Abbrechen"), CloseChoice.Cancel, false));
    }
}
