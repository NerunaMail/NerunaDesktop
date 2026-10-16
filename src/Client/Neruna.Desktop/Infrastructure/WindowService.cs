using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Neruna.Desktop.ViewModels;
using Neruna.Desktop.Views;

namespace Neruna.Desktop.Infrastructure;

/// <summary>Opens additional windows, so view models never create views themselves.</summary>
internal interface IWindowService
{
    void ShowCompose(ComposeViewModel compose);

    /// <summary>Drafts open in their own windows (asked about when Neruna closes).</summary>
    IReadOnlyList<ComposeViewModel> OpenComposes { get; }

    void ShowMessage(MessageWindowViewModel message);

    /// <returns>The edited signature, or null when cancelled.</returns>
    Task<Neruna.Core.Mail.Signature?> ShowSignatureEditorAsync(SignatureEditorViewModel editor);

    /// <summary>An event in a window of its own (from a mail being written); true if it was saved or deleted.</summary>
    Task<bool> ShowEventEditorAsync(EventEditorViewModel editor);
}

internal sealed class WindowService(UiLayout layout) : IWindowService
{
    private readonly List<ComposeViewModel> _composes = [];

    public IReadOnlyList<ComposeViewModel> OpenComposes => _composes;

    public void ShowCompose(ComposeViewModel compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        var window = new ComposeWindow { DataContext = compose };
        _composes.Add(compose);
        window.Closed += (_, _) => _composes.Remove(compose);
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

    public async Task<bool> ShowEventEditorAsync(EventEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var changed = false;
        // Above the window the mail is written in (the main window or its own); the icon comes from there.
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = lifetime?.Windows.FirstOrDefault(w => w.IsActive) ?? lifetime?.MainWindow;
        var window = new Window
        {
            Title = Neruna.Core.Localization.Texts.T("Termin"),
            Icon = owner?.Icon,
            Width = 640,
            Height = 760,
            MinWidth = 480,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = new EventEditorView { DataContext = editor, Margin = new Avalonia.Thickness(24, 18) } },
        };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("PaneBackgroundBrush"));
        editor.Finished += (_, saved) =>
        {
            changed = saved;
            window.Close();
        };

        if (owner is not null)
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

        return changed;
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
