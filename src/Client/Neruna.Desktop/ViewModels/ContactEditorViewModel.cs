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

/// <summary>An S/MIME certificate kept with the contact: whose, until when, and whether it can be used to encrypt.</summary>
internal sealed record ContactCertificateRow(byte[] Der, string Name, string Details, string? Warning)
{
    public bool HasWarning => Warning is not null;

    public static ContactCertificateRow? From(byte[] der, IReadOnlyCollection<string> contactEmails)
    {
        Org.BouncyCastle.X509.X509Certificate certificate;
        try
        {
            certificate = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(der);
        }
        catch (Exception ex) when (ex is Org.BouncyCastle.Security.Certificates.CertificateException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }

        if (certificate is null)
        {
            return null;
        }

        var info = Neruna.Core.Security.CertificateParser.Describe(certificate, false, Neruna.Core.Security.CertificateSource.Contact, DateTimeOffset.Now);
        var status = info.StatusAt(DateTimeOffset.Now) switch
        {
            Neruna.Core.Security.CertificateStatus.Expired => T("abgelaufen am {0}"),
            Neruna.Core.Security.CertificateStatus.NotYetValid => T("gültig ab {0}"),
            Neruna.Core.Security.CertificateStatus.ExpiringSoon => T("läuft bald ab: {0}"),
            _ => T("gültig bis {0}"),
        };
        var date = (info.StatusAt(DateTimeOffset.Now) == Neruna.Core.Security.CertificateStatus.NotYetValid ? info.NotBefore : info.NotAfter)
            .LocalDateTime.ToString("d", Neruna.Core.Localization.Texts.Culture);
        var details = string.Join(" · ", new[] { string.Join(", ", info.EmailAddresses), string.Format(Neruna.Core.Localization.Texts.Culture, status, date), info.IssuerName }
            .Where(p => p.Length > 0));
        var warning = !info.CanEncrypt ? T("Nicht zum Verschlüsseln geeignet.")
            : contactEmails.Count > 0 && !info.EmailAddresses.Any(a => contactEmails.Contains(a, StringComparer.OrdinalIgnoreCase))
                ? T("Gilt für keine Adresse dieses Kontakts.")
                : null;
        return new ContactCertificateRow(der, info.SubjectName, details, warning);
    }
}

/// <summary>Create/edit/delete one contact. Properties the form does not show are preserved (see <see cref="ContactDraft"/>).</summary>
internal sealed partial class ContactEditorViewModel : ViewModelBase
{
    private readonly ContactController _contacts;
    private readonly IFileService _files;
    private readonly (AddressBookInfo AddressBook, string RemoteId)? _existing;
    private ContactPhoto? _photo;
    private bool _photoChanged;
    private bool _certificatesChanged;

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
        Birthday = draft.Birthday?.ToInput(Neruna.Core.Localization.Texts.Culture) ?? string.Empty;

        foreach (var email in draft.Emails.DefaultIfEmpty(new ContactField(string.Empty)))
        {
            Emails.Add(new EditableField(FieldKind.EmailKinds, email.Value, email.Kind));
        }

        foreach (var phone in draft.Phones.DefaultIfEmpty(new ContactField(string.Empty)))
        {
            Phones.Add(new EditableField(FieldKind.PhoneKinds, phone.Value, phone.Kind));
        }

        _certificateDers = [.. draft.Certificates ?? []];
        ShowCertificates();
    }

    private List<byte[]> _certificateDers;

    /// <summary>S/MIME certificates kept with the contact (vCard KEY; Exchange/Microsoft 365: the contact's certificates).</summary>
    public ObservableCollection<ContactCertificateRow> Certificates { get; } = [];

    public bool HasCertificates => Certificates.Count > 0;

    public bool NoCertificates => !HasCertificates;

    [RelayCommand]
    private async Task AddCertificateAsync()
    {
        Error = null;
        foreach (var path in await _files.PickFilesAsync(T("Zertifikat des Kontakts wählen (.cer, .crt, .pem, .der, .p7b)")))
        {
            IReadOnlyList<Org.BouncyCastle.X509.X509Certificate> found;
            try
            {
                found = Neruna.Core.Security.CertificateParser.ReadCertificates(await File.ReadAllBytesAsync(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                Error = F("«{0}» konnte nicht gelesen werden: {1}", Path.GetFileName(path), ex.Message);
                continue;
            }

            // Only the person's own certificates; the issuer's (in a .p7b chain) do not belong to the contact.
            var people = found.Where(c => Neruna.Core.Security.CertificateParser.EmailAddresses(c).Count > 0).ToList();
            if (people.Count == 0)
            {
                Error = F("«{0}» enthält kein Zertifikat mit E-Mail-Adresse (eine .p12/.pfx mit privatem Schlüssel gehört unter Einstellungen → Zertifikate).", Path.GetFileName(path));
                continue;
            }

            foreach (var der in people.Select(c => c.GetEncoded()).Where(d => !_certificateDers.Any(k => k.AsSpan().SequenceEqual(d))))
            {
                _certificateDers.Add(der);
                _certificatesChanged = true;
            }
        }

        ShowCertificates();
    }

    [RelayCommand]
    private void RemoveCertificate(ContactCertificateRow? row)
    {
        if (row is not null && _certificateDers.RemoveAll(d => d.AsSpan().SequenceEqual(row.Der)) > 0)
        {
            _certificatesChanged = true;
            ShowCertificates();
        }
    }

    private void ShowCertificates()
    {
        var emails = Emails.Select(e => e.Value.Trim()).Where(e => e.Length > 0).ToList();
        Certificates.Clear();
        foreach (var row in _certificateDers.Select(d => ContactCertificateRow.From(d, emails)).OfType<ContactCertificateRow>())
        {
            Certificates.Add(row);
        }

        OnPropertyChanged(nameof(HasCertificates));
        OnPropertyChanged(nameof(NoCertificates));
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

    /// <summary>Day and month, optionally the year: 15.04.1985 or 15.04.</summary>
    [ObservableProperty]
    public partial string Birthday { get; set; } = string.Empty;

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

        var birthday = Neruna.Core.Contacts.ContactBirthday.ParseInput(Birthday);
        if (birthday is null && !string.IsNullOrWhiteSpace(Birthday))
        {
            Error = T("Der Geburtstag ist ungültig – bitte als Tag.Monat.Jahr eingeben, das Jahr kann fehlen (z. B. 15.04.1985 oder 15.04.).");
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
            Birthday = birthday,
            Certificates = _certificatesChanged ? _certificateDers : null,
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
