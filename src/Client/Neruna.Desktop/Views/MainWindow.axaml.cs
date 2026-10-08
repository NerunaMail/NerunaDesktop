using Avalonia.Controls;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class MainWindow : Window
{
    private bool _mayClose;

    public MainWindow()
    {
        InitializeComponent();
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
