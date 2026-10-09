using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Avalonia.Media.Imaging;
using Neruna.Core.Contacts;
using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

internal sealed record FieldKind(string? Value, string Label)
{
    public static IReadOnlyList<FieldKind> EmailKinds { get; } = [new(null, "—"), new("work", T("Geschäftlich")), new("home", T("Privat"))];

    public static IReadOnlyList<FieldKind> PhoneKinds { get; } =
        [new(null, "—"), new("work", "Geschäftlich"), new("home", "Privat"), new("cell", "Mobil"), new("fax", "Fax")];

    public static FieldKind Find(IReadOnlyList<FieldKind> kinds, string? value) =>
        kinds.FirstOrDefault(k => string.Equals(k.Value, value, StringComparison.OrdinalIgnoreCase)) ?? new FieldKind(value, value ?? "—");
}

internal sealed partial class EditableField(IReadOnlyList<FieldKind> kinds, string value, string? kind) : ObservableObject
{
    public IReadOnlyList<FieldKind> Kinds { get; } = kinds;

    [ObservableProperty]
    public partial string Value { get; set; } = value;

    [ObservableProperty]
    public partial FieldKind Kind { get; set; } = FieldKind.Find(kinds, kind);

    public ContactField ToField() => new(Value.Trim(), Kind.Value);
}

/// <summary>Create/edit/delete one contact. Properties the form does not show are preserved (see <see cref="ContactDraft"/>).</summary>
internal sealed partial class ContactEditorViewModel : ViewModelBase
{
    private readonly ContactController _contacts;
    private readonly IFileService _files;
    private readonly (AddressBookInfo AddressBook, string RemoteId)? _existing;
    private ContactPhoto? _photo;
    private bool _photoChanged;

    public ContactEditorViewModel(
        ContactController contacts,
        IFileService files,
        IReadOnlyList<AddressBookInfo> writableAddressBooks,
        ContactDraft draft,
        (AddressBookInfo AddressBook, string RemoteId)? existing)
    {
        _contacts = contacts;
        _files = files;
        _existing = existing;
        _photo = draft.Photo;
        Photo = ContactPhotoBitmap.From(_photo);

        IsReadOnly = existing?.AddressBook.IsReadOnly ?? false;
        AddressBooks = IsReadOnly ? [existing!.Value.AddressBook] : writableAddressBooks;
        SelectedAddressBook = existing is { } e
            ? AddressBooks.FirstOrDefault(b => b.ConnectionId == e.AddressBook.ConnectionId && b.RemoteId == e.AddressBook.RemoteId) ?? AddressBooks.FirstOrDefault()
            : AddressBooks.FirstOrDefault();

        GivenName = draft.GivenName ?? string.Empty;
        FamilyName = draft.FamilyName ?? string.Empty;
        Organization = draft.Organization ?? string.Empty;
        Title = draft.Title ?? string.Empty;
        Note = draft.Note ?? string.Empty;

        foreach (var email in draft.Emails.DefaultIfEmpty(new ContactField(string.Empty)))
        {
            Emails.Add(new EditableField(FieldKind.EmailKinds, email.Value, email.Kind));
        }

        foreach (var phone in draft.Phones.DefaultIfEmpty(new ContactField(string.Empty)))
        {
            Phones.Add(new EditableField(FieldKind.PhoneKinds, phone.Value, phone.Kind));
        }
    }

    public event EventHandler<bool>? Finished;

    public IReadOnlyList<AddressBookInfo> AddressBooks { get; }

    public bool IsReadOnly { get; }

    public bool IsExisting => _existing is not null;

    public string Heading => IsReadOnly ? T("Kontakt (schreibgeschützt)") : IsExisting ? T("Kontakt bearbeiten") : T("Neuer Kontakt");

    public bool NoAddressBook => AddressBooks.Count == 0;

    public ObservableCollection<EditableField> Emails { get; } = [];

    public ObservableCollection<EditableField> Phones { get; } = [];

    [ObservableProperty]
    public partial AddressBookInfo? SelectedAddressBook { get; set; }

    /// <summary>Contact picture (vCard PHOTO), shown in the list and the details.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoto))]
    public partial Bitmap? Photo { get; set; }

    public bool HasPhoto => Photo is not null;

    [ObservableProperty]
    public partial string GivenName { get; set; }

    [ObservableProperty]
    public partial string FamilyName { get; set; }

    [ObservableProperty]
    public partial string Organization { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Note { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool ConfirmDelete { get; set; }

    /// <summary>Any picture becomes a 256×256 JPEG, cropped to a centered square.</summary>
    [RelayCommand]
    private async Task ChoosePhotoAsync()
    {
        var path = (await _files.PickFilesAsync(T("Kontaktbild wählen"))).FirstOrDefault();
        if (path is null)
        {
            return;
        }

        var jpeg = await Task.Run(() => AvatarImage.FromFile(path));
        if (jpeg is null)
        {
            Error = F("«{0}» ist kein lesbares Bild.", Path.GetFileName(path));
            return;
        }

        Error = null;
        _photo = new ContactPhoto(jpeg, "image/jpeg");
        _photoChanged = true;
        Photo = ContactPhotoBitmap.From(_photo);
    }

    [RelayCommand]
    private void RemovePhoto()
    {
        _photo = null;
        _photoChanged = true;
        Photo = null;
    }

    [RelayCommand]
    private void AddEmail() => Emails.Add(new EditableField(FieldKind.EmailKinds, string.Empty, null));

    [RelayCommand]
    private void AddPhone() => Phones.Add(new EditableField(FieldKind.PhoneKinds, string.Empty, null));

    [RelayCommand]
    private void RemoveField(EditableField field)
    {
        Emails.Remove(field);
        Phones.Remove(field);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Error = null;
        if (SelectedAddressBook is null)
        {
            Error = T("Kein beschreibbares Adressbuch vorhanden.");
            return;
        }

        var draft = new ContactDraft(
            GivenName.Trim(),
            FamilyName.Trim(),
            Organization.Trim(),
            Title.Trim(),
            Emails.Select(e => e.ToField()).Where(f => f.Value.Length > 0).ToList(),
            Phones.Select(p => p.ToField()).Where(f => f.Value.Length > 0).ToList(),
            Note.Trim())
        {
            Photo = _photo,
            ReplacePhoto = _photoChanged,
        };

        if (draft.DisplayName.Length == 0)
        {
            Error = T("Bitte mindestens einen Namen, eine Firma oder eine E-Mail-Adresse angeben.");
            return;
        }

        await RunAsync(() => _contacts.SaveContactAsync(SelectedAddressBook, draft, _existing));
    }

    private bool CanSave() => !IsBusy && !IsReadOnly;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task DeleteAsync()
    {
        if (_existing is not { } existing)
        {
            return;
        }

        if (!ConfirmDelete)
        {
            ConfirmDelete = true;
            return;
        }

        await RunAsync(() => _contacts.DeleteContactAsync(existing.AddressBook, existing.RemoteId));
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);

    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true;
        try
        {
            await operation();
            Finished?.Invoke(this, true);
        }
        catch (RemoteConflictException)
        {
            Error = T("Der Kontakt wurde inzwischen auf dem Server geändert. Bitte synchronisieren (F5) und erneut bearbeiten.");
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
}
