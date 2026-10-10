using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Contacts;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Adressbücher verwalten": asks every CardDAV server again which address books exist and lets the user choose which
/// ones Neruna shows and synchronizes – e.g. the shared domain address book only once when two accounts of the same
/// organisation offer it.
/// </summary>
internal sealed partial class AddressBookSelectionViewModel(ContactController contacts) : ViewModelBase
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
            foreach (var source in await contacts.GetSourcesAsync())
            {
                var group = new AddressBookSourceGroup(source);
                Sources.Add(group);
                try
                {
                    foreach (var candidate in await contacts.DiscoverAsync(source.Connection))
                    {
                        group.AddressBooks.Add(new AddressBookChoiceItem(candidate));
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

internal sealed class AddressBookSourceGroup(AddressBookSource source) : ObservableObject
{
    private string? _error;

    public AddressBookSource Source { get; } = source;

    public string Title => Source.Account.Title;

    public string Server => Source.Server.Length > 0 ? Source.Server : Source.Connection.ProviderId;

    public ObservableCollection<AddressBookChoiceItem> AddressBooks { get; } = [];

    public string? Error
    {
        get => _error;
        set => SetProperty(ref _error, value);
    }
}

internal sealed partial class AddressBookChoiceItem(AddressBookCandidate candidate) : ObservableObject
{
    public AddressBookCandidate Candidate { get; } = candidate;

    public string Name => Candidate.AddressBook.Name;

    public bool IsNew => Candidate.IsNew;

    public bool IsReadOnly => Candidate.AddressBook.IsReadOnly;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = candidate.IsSelected;
}
