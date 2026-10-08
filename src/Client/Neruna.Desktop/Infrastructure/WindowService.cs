using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Neruna.Desktop.ViewModels;
using Neruna.Desktop.Views;

namespace Neruna.Desktop.Infrastructure;

/// <summary>Opens additional windows, so view models never create views themselves.</summary>
internal interface IWindowService
{
    void ShowCompose(ComposeViewModel compose);

    void ShowMessage(MessageWindowViewModel message);

    /// <returns>The edited signature, or null when cancelled.</returns>
    Task<Neruna.Core.Mail.Signature?> ShowSignatureEditorAsync(SignatureEditorViewModel editor);
}

internal sealed class WindowService(UiLayout layout) : IWindowService
{
    public void ShowCompose(ComposeViewModel compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        var window = new ComposeWindow { DataContext = compose };
        layout.TrackWindow(window, "window.compose", withPosition: false);
        compose.Closed += (_, _) => window.Close();

        if ((Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    public void ShowMessage(MessageWindowViewModel message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var window = new MessageWindow { DataContext = message };
        layout.TrackWindow(window, "window.message", withPosition: false);
        message.CloseRequested += (_, _) => window.Close();
        if ((Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    public async Task<Neruna.Core.Mail.Signature?> ShowSignatureEditorAsync(SignatureEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        Neruna.Core.Mail.Signature? result = null;
        var window = new SignatureEditorWindow { DataContext = editor };
        editor.Finished += (_, saved) =>
        {
            result = saved;
            window.Close();
        };

        if ((Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is Window owner)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }

        return result;
    }
}
