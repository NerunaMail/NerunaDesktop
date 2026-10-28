using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Contacts;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Adressbücher verwalten": asks every CardDAV server again which address books exist and lets the user choose which
/// ones Neruna shows and synchronizes – e.g. the shared domain address book only once when two accounts of the same
/// organisation offer it. The arrows set the order of the address books within their account in the contact list.
/// </summary>
internal sealed partial class AddressBookSelectionViewModel(ContactController contacts, ISettingsStore settings) : ViewModelBase
{
    public event EventHandler<bool>? Finished;

    public ObservableCollection<AddressBookSourceGroup> Sources { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RefreshCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool HasNoSources => !IsBusy && Sources.Count == 0;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(HasNoSources));

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            Sources.Clear();
            // As in the contact list: the chosen order first, the others by name.
            var order = CalendarViewModel.ParseKeys(await settings.GetAsync(SettingKeys.AddressBookOrder));
            foreach (var source in await contacts.GetSourcesAsync())
            {
                var group = new AddressBookSourceGroup(source);
                Sources.Add(group);
                try
                {
                    foreach (var candidate in (await contacts.DiscoverAsync(source.Connection))
                                 .OrderBy(c => order.IndexOf(ContactsViewModel.OrderKey(source.Connection.Id, c.AddressBook.RemoteId)) is var at and >= 0 ? at : order.Count))
                    {
                        group.AddressBooks.Add(new AddressBookChoiceItem(group, candidate));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    group.Error = T("Server nicht erreichbar: ") + ex.Message;
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ApplyAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            foreach (var group in Sources.Where(g => g.Error is null && g.AddressBooks.Count > 0))
            {
                await contacts.SetSelectionAsync(
                    group.Source.Connection,
                    group.AddressBooks.Where(b => b.IsSelected).Select(b => b.Candidate.AddressBook.RemoteId).ToList(),
                    group.AddressBooks.Select(b => b.Candidate.AddressBook.RemoteId).ToList());
            }

            // The order of the address books listed here; those of servers not reachable now keep their place behind them.
            var listed = Sources.SelectMany(g => g.AddressBooks.Select(b => ContactsViewModel.OrderKey(g.Source.Connection.Id, b.Candidate.AddressBook.RemoteId))).ToList();
            var kept = CalendarViewModel.ParseKeys(await settings.GetAsync(SettingKeys.AddressBookOrder)).Where(k => !listed.Contains(k));
            await settings.SetAsync(SettingKeys.AddressBookOrder, System.Text.Json.JsonSerializer.Serialize(listed.Concat(kept)));

            Finished?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = T("Übernehmen fehlgeschlagen: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);

    private bool IsIdle() => !IsBusy;
}

internal sealed class AddressBookSourceGroup : ObservableObject
{
    private string? _error;

    public AddressBookSourceGroup(AddressBookSource source)
    {
        Source = source;
        AddressBooks.CollectionChanged += (_, _) => UpdateMoves();
    }

    /// <summary>Moves an address book one place up or down within the account.</summary>
    public void Move(AddressBookChoiceItem item, int delta)
    {
        var index = AddressBooks.IndexOf(item);
        var target = index + delta;
        if (index >= 0 && target >= 0 && target < AddressBooks.Count)
        {
            AddressBooks.Move(index, target);
        }
    }

    private void UpdateMoves()
    {
        for (var i = 0; i < AddressBooks.Count; i++)
        {
            AddressBooks[i].CanMoveUp = i > 0;
            AddressBooks[i].CanMoveDown = i < AddressBooks.Count - 1;
        }
    }

    public AddressBookSource Source { get; }

    public string Title => Source.Account.Title;

    public string Server => Source.Server.Length > 0 ? Source.Server : Source.Connection.ProviderId;

    public ObservableCollection<AddressBookChoiceItem> AddressBooks { get; } = [];

    public string? Error
    {
        get => _error;
        set => SetProperty(ref _error, value);
    }
}

internal sealed partial class AddressBookChoiceItem(AddressBookSourceGroup group, AddressBookCandidate candidate) : ObservableObject
{
    public AddressBookCandidate Candidate { get; } = candidate;

    public string Name => Candidate.AddressBook.Name;

    public bool IsNew => Candidate.IsNew;

    /// <summary>"geteilt von Lea Keller": another person's address book, found through delegation.</summary>
    public string? SharedByText => Candidate.AddressBook.SharedBy is { } owner ? F("geteilt von {0}", owner) : null;

    public bool IsReadOnly => Candidate.AddressBook.IsReadOnly;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = candidate.IsSelected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    public partial bool CanMoveDown { get; set; }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => group.Move(this, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => group.Move(this, +1);
}
