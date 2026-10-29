using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Contacts;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Duplikate": contacts that are probably the same person (same name, address or number), each set with the
/// contact to keep chosen – "Zusammenführen" puts everything of the others into it and deletes them.
/// </summary>
internal sealed partial class ContactDuplicatesViewModel : ViewModelBase
{
    private readonly ContactController _contacts;
    private bool _changed;

    public ContactDuplicatesViewModel(ContactController contacts, IReadOnlyList<IReadOnlyList<ContactEntry>> duplicates)
    {
        _contacts = contacts;
        foreach (var set in duplicates)
        {
            Sets.Add(new DuplicateSet(this, set));
        }
    }

    /// <summary>True when something was merged (the list reloads).</summary>
    public event EventHandler<bool>? Finished;

    public ObservableCollection<DuplicateSet> Sets { get; } = [];

    public bool HasNone => Sets.Count == 0;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    public partial bool IsBusy { get; set; }

    internal async Task MergeAsync(DuplicateSet set)
    {
        if (IsBusy || set.Keep is not { } keep)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            await _contacts.MergeContactsAsync(keep.Entry, [.. set.Contacts.Where(c => c != keep).Select(c => c.Entry)]);
            _changed = true;
            Sets.Remove(set);
            OnPropertyChanged(nameof(HasNone));
        }
        catch (Exception ex) when (ex is RemoteConflictException or HttpRequestException or IOException or InvalidOperationException)
        {
            Error = T("Zusammenführen fehlgeschlagen: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>"Nicht dasselbe": the set leaves the list (it is found again next time).</summary>
    internal void Skip(DuplicateSet set) => Sets.Remove(set);

    private bool IsIdle() => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Close() => Finished?.Invoke(this, _changed);
}

/// <summary>Contacts that are probably the same person.</summary>
internal sealed partial class DuplicateSet : ObservableObject
{
    private readonly ContactDuplicatesViewModel _owner;

    public DuplicateSet(ContactDuplicatesViewModel owner, IReadOnlyList<ContactEntry> entries)
    {
        _owner = owner;
        // Keep the one with the most in it; the others are merged into it.
        var richest = entries.OrderByDescending(e => e.Card.Emails.Count + e.Card.Phones.Count + (e.Card.Photo is null ? 0 : 1)).First();
        Contacts = [.. entries.Select(e => new DuplicateContact(this, e, e == richest))];
    }

    public IReadOnlyList<DuplicateContact> Contacts { get; }

    public string Title => Contacts[0].Name;

    public DuplicateContact? Keep => Contacts.FirstOrDefault(c => c.IsKeep);

    [RelayCommand]
    private Task MergeAsync() => _owner.MergeAsync(this);

    [RelayCommand]
    private void Skip() => _owner.Skip(this);
}

internal sealed partial class DuplicateContact(DuplicateSet set, ContactEntry entry, bool keep) : ObservableObject
{
    public ContactEntry Entry { get; } = entry;

    /// <summary>The radio buttons of one set share this group name.</summary>
    public string GroupName { get; } = "dup-" + set.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Name => Entry.Card.DisplayName;

    public string AddressBook => Entry.AddressBook.Name;

    /// <summary>What the contact has, one line each: addresses, numbers, company.</summary>
    public string Details => string.Join("\n", Entry.Card.EmailAddresses.Concat(Entry.Card.PhoneNumbers)
        .Concat(new[] { Entry.Card.Organization }.OfType<string>().Where(o => o.Length > 0)));

    [ObservableProperty]
    public partial bool IsKeep { get; set; } = keep;
}
