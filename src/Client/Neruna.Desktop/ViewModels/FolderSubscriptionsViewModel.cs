using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Accounts;
using Neruna.Core.Mail;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Right-click on an account → "Ordner abonnieren …": which of the server's folders Neruna shows. Saved as IMAP
/// subscriptions, so other mail programs see the same; hidden folders leave this device with their downloaded mail.
/// </summary>
internal sealed partial class FolderSubscriptionsViewModel(MailController mail, ServiceConnection connection, string accountTitle) : ViewModelBase
{
    public event EventHandler<bool>? Finished;

    public string Title => F("Ordner abonnieren – {0}", accountTitle);

    public ObservableCollection<FolderSubscriptionItem> Folders { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    public async Task LoadAsync()
    {
        try
        {
            var subscriptions = await mail.GetSubscriptionsAsync(connection);
            var byId = subscriptions.ToDictionary(s => s.Folder.RemoteId, StringComparer.Ordinal);
            int Depth(MailFolder folder) => folder.ParentRemoteId is { } parent && byId.TryGetValue(parent, out var p) ? Depth(p.Folder) + 1 : 0;

            // Inbox first, then the tree in path order (children under their parent).
            foreach (var subscription in subscriptions.OrderBy(s => s.Folder.Role == FolderRole.Inbox ? 0 : 1).ThenBy(s => s.Folder.RemoteId, StringComparer.OrdinalIgnoreCase))
            {
                Folders.Add(new FolderSubscriptionItem(subscription, MailFolderNode.DisplayName(subscription.Folder), Depth(subscription.Folder)));
            }
        }
        catch (Exception ex) when (ex is NotSupportedException)
        {
            Error = T("Dieser Kontotyp kennt keine Ordner-Abos.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = T("Die Ordner konnten nicht vom Server geladen werden: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAll(bool value)
    {
        foreach (var folder in Folders.Where(f => !f.Required))
        {
            folder.IsSubscribed = value;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            await mail.SetSubscriptionsAsync(connection, [.. Folders.Where(f => f.IsSubscribed).Select(f => f.RemoteId)]);
            Finished?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = T("Speichern fehlgeschlagen: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => !IsBusy && Folders.Count > 0;

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);
}

internal sealed partial class FolderSubscriptionItem(FolderSubscription subscription, string name, int depth) : ObservableObject
{
    public string RemoteId => subscription.Folder.RemoteId;

    public string Name { get; } = name;

    public FolderRole Role => subscription.Folder.Role;

    /// <summary>Indent per level of the folder tree.</summary>
    public Avalonia.Thickness Indent { get; } = new(depth * 22, 0, 0, 0);

    /// <summary>Inbox, sent, drafts, trash: always shown.</summary>
    public bool Required => subscription.Required;

    public bool CanChange => !Required;

    [ObservableProperty]
    public partial bool IsSubscribed { get; set; } = subscription.Subscribed;
}
