using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// A message in its own window (double-click in the list). Shows the same reading pane; replies and forwards open in
/// their own compose window. The mail page does the actual work through the callbacks.
/// </summary>
internal sealed partial class MessageWindowViewModel(
    string subject,
    Neruna.Desktop.Infrastructure.UiPreferences preferences,
    Func<MessageWindowViewModel, Task> reply,
    Func<MessageWindowViewModel, Task> replyAll,
    Func<MessageWindowViewModel, Task> forward,
    Func<MessageWindowViewModel, Task<bool>> delete) : ViewModelBase
{
    /// <summary>Raised when the window should close (message deleted).</summary>
    public event EventHandler? CloseRequested;

    public string WindowTitle { get; } = string.IsNullOrWhiteSpace(subject) ? "(kein Betreff)" : subject;

    public Neruna.Desktop.Infrastructure.UiPreferences Preferences => preferences;

    /// <summary>The reading pane; replaced when the user allows remote images.</summary>
    [ObservableProperty]
    public partial ReadingPaneViewModel? Pane { get; set; }

    [RelayCommand]
    private Task ReplyAsync() => reply(this);

    [RelayCommand]
    private Task ReplyAllAsync() => replyAll(this);

    [RelayCommand]
    private Task ForwardAsync() => forward(this);

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (await delete(this))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
