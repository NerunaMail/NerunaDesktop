namespace Neruna.Desktop.ViewModels;

/// <summary>One desktop notification ("Neue E-Mail"). <see cref="Open"/> runs when it is clicked.</summary>
internal sealed class NotificationViewModel(string title, string sender, string subject, string? preview, Func<Task> open) : ViewModelBase
{
    public string Title { get; } = title;

    public string Sender { get; } = sender;

    public string Subject { get; } = subject;

    public string? Preview { get; } = preview;

    public bool HasPreview => !string.IsNullOrWhiteSpace(Preview);

    public Func<Task> Open { get; } = open;
}
