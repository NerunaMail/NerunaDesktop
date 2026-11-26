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
        [new(null, "—"), new("work", T("Geschäftlich")), new("home", T("Privat")), new("cell", T("Mobil")), new("fax", T("Fax"))];

    public static IReadOnlyList<FieldKind> AddressKinds { get; } = [new(null, "—"), new("work", T("Geschäftlich")), new("home", T("Privat"))];

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

/// <summary>A postal address in the editor; post office box, extended part and region it does not show are kept.</summary>
internal sealed partial class EditableAddress(ContactAddress original) : ObservableObject
{
    public ContactAddress Original { get; } = original;

    public IReadOnlyList<FieldKind> Kinds { get; } = FieldKind.AddressKinds;

    [ObservableProperty]
    public partial string Street { get; set; } = original.Street ?? string.Empty;

    [ObservableProperty]
    public partial string PostalCode { get; set; } = original.PostalCode ?? string.Empty;

    [ObservableProperty]
    public partial string City { get; set; } = original.City ?? string.Empty;

    [ObservableProperty]
    public partial string Country { get; set; } = original.Country ?? string.Empty;

    [ObservableProperty]
    public partial FieldKind Kind { get; set; } = FieldKind.Find(FieldKind.AddressKinds, original.Kind);

    public ContactAddress ToAddress() => Original with
    {
        Street = Value(Street),
        PostalCode = Value(PostalCode),
        City = Value(City),
        Country = Value(Country),
        Kind = Kind.Value,
    };

    private static string? Value(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>An existing contact to complete with the sender of a mail.</summary>
internal sealed record ContactChoiceRow(ContactEntry Entry, bool IsSuggested)
{
    public string Name => Entry.Card.DisplayName;

    public string Detail => string.Join(" · ", new[] { Entry.Card.Organization, Entry.Card.Emails.FirstOrDefault()?.Value, Entry.AddressBook.Name }
        .Where(part => !string.IsNullOrWhiteSpace(part)));
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

/// <summary>A public PGP key kept with the contact: whose, its fingerprint, until when, and whether it can encrypt.</summary>
internal sealed record ContactPgpKeyRow(byte[] Key, string Name, string Details, string Fingerprint, string? Warning)
{
    public bool HasWarning => Warning is not null;

    public static ContactPgpKeyRow? From(byte[] key, IReadOnlyCollection<string> contactEmails)
    {
        Neruna.Core.Security.PgpKeyInfo info;
        try
        {
            info = Neruna.Core.Security.PgpKeyParser.Describe(new Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKeyRing(key), false, Neruna.Core.Security.PgpKeySource.Contact, true, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is IOException or Org.BouncyCastle.Bcpg.OpenPgp.PgpException or ArgumentException)
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var validity = info.IsRevoked ? T("widerrufen")
            : info.Expires is not { } end ? T("unbegrenzt gültig")
            : string.Format(Neruna.Core.Localization.Texts.Culture, info.StatusAt(now) == Neruna.Core.Security.CertificateStatus.Expired ? T("abgelaufen am {0}") : T("gültig bis {0}"),
                end.LocalDateTime.ToString("d", Neruna.Core.Localization.Texts.Culture));
        var details = string.Join(" · ", new[] { string.Join(", ", info.EmailAddresses), info.Algorithm, validity }.Where(p => p.Length > 0));
        var warning = !info.CanEncrypt || !info.IsUsableAt(now) ? T("Nicht zum Verschlüsseln geeignet.")
            : contactEmails.Count > 0 && !info.EmailAddresses.Any(a => contactEmails.Contains(a, StringComparer.OrdinalIgnoreCase))
                ? T("Gilt für keine Adresse dieses Kontakts.")
                : null;
        return new ContactPgpKeyRow(key, info.Name, details, info.FingerprintText, warning);
    }
}

/// <summary>Create/edit/delete one contact. Properties the form does not show are preserved (see <see cref="ContactDraft"/>).</summary>
internal sealed partial class ContactEditorViewModel : ViewModelBase
{
    private readonly ContactController _contacts;
    private readonly IFileService _files;
    private (AddressBookInfo AddressBook, string RemoteId)? _existing;
    private IReadOnlyList<ContactAddress> _loadedAddresses = [];
    private IReadOnlyList<ContactField> _loadedWebsites = [];
    private bool _showsAddresses;
    private readonly ContactDraft? _fromMail;
    private IReadOnlyList<ContactEntry> _candidates = [];
    private ContactPhoto? _photo;
    private bool _photoChanged;
    private bool _certificatesChanged;
    private bool _pgpKeysChanged;

    /// <param name="fromMail">Opened from a mail ("Absender als Kontakt speichern"): the sender and what its signature
    /// shows. A new contact starts with it, or an existing one is chosen and completed with it.</param>
    public ContactEditorViewModel(
        ContactController contacts,
        IFileService files,
        IReadOnlyList<AddressBookInfo> writableAddressBooks,
        ContactDraft draft,
        (AddressBookInfo AddressBook, string RemoteId)? existing,
        ContactDraft? fromMail = null)
    {
        _contacts = contacts;
        _files = files;
        _fromMail = fromMail;

        IsReadOnly = existing?.AddressBook.IsReadOnly ?? false;
        AddressBooks = IsReadOnly ? [existing!.Value.AddressBook] : writableAddressBooks;
        Load(draft, existing);
    }

    /// <summary>Shows <paramref name="draft"/> (the contact or the new one); <paramref name="existing"/> is where it is saved.</summary>
    private void Load(ContactDraft draft, (AddressBookInfo AddressBook, string RemoteId)? existing)
    {
        _existing = existing;
        _photo = draft.Photo;
        _photoChanged = false;
        Photo = ContactPhotoBitmap.From(_photo);
        SelectedAddressBook = existing is { } e
            ? AddressBooks.FirstOrDefault(b => b.ConnectionId == e.AddressBook.ConnectionId && b.RemoteId == e.AddressBook.RemoteId) ?? AddressBooks.FirstOrDefault()
            : SelectedAddressBook ?? AddressBooks.FirstOrDefault();

        GivenName = draft.GivenName ?? string.Empty;
        FamilyName = draft.FamilyName ?? string.Empty;
        Organization = draft.Organization ?? string.Empty;
        Title = draft.Title ?? string.Empty;
        Note = draft.Note ?? string.Empty;
        Birthday = draft.Birthday?.ToInput(Neruna.Core.Localization.Texts.Culture) ?? string.Empty;

        Emails.Clear();
        foreach (var email in draft.Emails.DefaultIfEmpty(new ContactField(string.Empty)))
        {
            Emails.Add(new EditableField(FieldKind.EmailKinds, email.Value, email.Kind));
        }

        Phones.Clear();
        foreach (var phone in draft.Phones.DefaultIfEmpty(new ContactField(string.Empty)))
        {
            Phones.Add(new EditableField(FieldKind.PhoneKinds, phone.Value, phone.Kind));
        }

        // Addresses and web addresses: those of the card (unchanged ones stay exactly as written there).
        _showsAddresses = draft.Addresses is not null || draft.Websites is not null || existing is null;
        _loadedAddresses = existing is null ? [] : draft.Addresses ?? [];
        _loadedWebsites = existing is null ? [] : draft.Websites ?? [];
        Addresses.Clear();
        foreach (var address in draft.Addresses ?? [])
        {
            Addresses.Add(new EditableAddress(address));
        }

        Websites.Clear();
        foreach (var site in draft.Websites ?? [])
        {
            Websites.Add(new EditableField(FieldKind.AddressKinds, site.Value, site.Kind));
        }

        _certificateDers = [.. draft.Certificates ?? []];
        _certificatesChanged = false;
        ShowCertificates();
        _pgpKeyData = [.. draft.PgpKeys ?? []];
        _pgpKeysChanged = false;
        ShowPgpKeys();

        OnPropertyChanged(nameof(IsExisting));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(CompletesExisting));
        OnPropertyChanged(nameof(HasAddresses));
    }

    private List<byte[]> _pgpKeyData = [];

    /// <summary>Public PGP keys kept with the contact (vCard KEY): they count as confirmed for this person.</summary>
    public ObservableCollection<ContactPgpKeyRow> PgpKeys { get; } = [];

    public bool HasPgpKeys => PgpKeys.Count > 0;

    public bool NoPgpKeys => !HasPgpKeys;

    [RelayCommand]
    private async Task AddPgpKeyAsync()
    {
        Error = null;
        foreach (var path in await _files.PickFilesAsync(T("PGP-Schlüssel des Kontakts wählen (.asc, .gpg, .pgp, .key)")))
        {
            IReadOnlyList<Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKeyRing> found;
            try
            {
                var (rings, secret) = Neruna.Core.Security.PgpKeyParser.Read(await File.ReadAllBytesAsync(path));
                found = [.. rings, .. secret.Select(Neruna.Core.Security.PgpKeyParser.PublicOf)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                Error = F("«{0}» enthält keinen PGP-Schlüssel.", Path.GetFileName(path));
                continue;
            }

            // Only the public part goes to the contact, even from a file that also holds a private key.
            foreach (var key in found.Select(r => r.GetEncoded()).Where(k => !_pgpKeyData.Any(d => d.AsSpan().SequenceEqual(k))))
            {
                _pgpKeyData.Add(key);
                _pgpKeysChanged = true;
            }
        }

        ShowPgpKeys();
    }

    [RelayCommand]
    private void RemovePgpKey(ContactPgpKeyRow? row)
    {
        if (row is not null && _pgpKeyData.RemoveAll(d => d.AsSpan().SequenceEqual(row.Key)) > 0)
        {
            _pgpKeysChanged = true;
            ShowPgpKeys();
        }
    }

    private void ShowPgpKeys()
    {
        var emails = Emails.Select(e => e.Value.Trim()).Where(e => e.Length > 0).ToList();
        PgpKeys.Clear();
        foreach (var row in _pgpKeyData.Select(d => ContactPgpKeyRow.From(d, emails)).OfType<ContactPgpKeyRow>())
        {
            PgpKeys.Add(row);
        }

        OnPropertyChanged(nameof(HasPgpKeys));
        OnPropertyChanged(nameof(NoPgpKeys));
    }

    private List<byte[]> _certificateDers = [];

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

    public string Heading => IsReadOnly ? T("Kontakt (schreibgeschützt)") : CompletesExisting ? T("Kontakt ergänzen") : IsExisting ? T("Kontakt bearbeiten") : T("Neuer Kontakt");

    public bool NoAddressBook => AddressBooks.Count == 0;

    public ObservableCollection<EditableField> Emails { get; } = [];

    public ObservableCollection<EditableField> Phones { get; } = [];

    public ObservableCollection<EditableAddress> Addresses { get; } = [];

    public bool HasAddresses => Addresses.Count > 0;

    public ObservableCollection<EditableField> Websites { get; } = [];

    /// <summary>Opened from a mail: a new contact, or an existing one to complete (chosen at the top).</summary>
    public bool OffersExisting => _fromMail is not null && !IsReadOnly;

    /// <summary>An existing contact is being completed with the sender.</summary>
    public bool CompletesExisting => OffersExisting && _existing is not null;

    public bool CreatesNew => OffersExisting && _existing is null;

    /// <summary>What the signature of the mail added ("bitte prüfen"), or null.</summary>
    public string? SignatureText { get; private set; }

    public bool HasSignatureText => SignatureText is not null;

    /// <summary>Search for the contact to complete: by name, organisation or address.</summary>
    [ObservableProperty]
    public partial string ExistingQuery { get; set; } = string.Empty;

    /// <summary>Contacts with the sender's name first (without a search), else those the search finds.</summary>
    public ObservableCollection<ContactChoiceRow> ExistingMatches { get; } = [];

    public bool HasExistingMatches => ExistingMatches.Count > 0;

    partial void OnExistingQueryChanged(string value) => ShowMatches();

    /// <summary>Opened from a mail: the contacts in writable address books, for choosing one to complete.</summary>
    public async Task LoadCandidatesAsync()
    {
        if (!OffersExisting)
        {
            return;
        }

        var writable = AddressBooks.Select(b => (b.ConnectionId, b.RemoteId)).ToHashSet();
        _candidates = [.. (await _contacts.SearchAsync(null)).Where(c => !c.Card.IsGroup && writable.Contains((c.AddressBook.ConnectionId, c.AddressBook.RemoteId)))];
        ShowMatches();
    }

    private void ShowMatches()
    {
        ExistingMatches.Clear();
        var query = ExistingQuery.Trim();
        var name = $"{_fromMail?.GivenName} {_fromMail?.FamilyName}".Trim();
        var rows = query.Length == 0
            ? _candidates.Where(c => name.Length > 0 && SameName(c.Card, name)).Select(c => new ContactChoiceRow(c, IsSuggested: true))
            : _candidates.Where(c => Matches(c.Card, query)).Take(8).Select(c => new ContactChoiceRow(c, IsSuggested: false));
        foreach (var row in rows)
        {
            ExistingMatches.Add(row);
        }

        OnPropertyChanged(nameof(HasExistingMatches));
    }

    private static bool SameName(ContactCard card, string name)
    {
        static string Normal(string s) => string.Join(' ', s.ToLowerInvariant().Split([' ', ',', '.', '"'], StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        return Normal(card.DisplayName) == Normal(name) || Normal($"{card.GivenName} {card.FamilyName}") == Normal(name);
    }

    private static bool Matches(ContactCard card, string query) =>
        card.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || (card.Organization?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || card.Emails.Any(e => e.Value.Contains(query, StringComparison.OrdinalIgnoreCase));

    /// <summary>Complete this contact: its fields with the sender's address and the signature's data added.</summary>
    [RelayCommand]
    private async Task ChooseExistingAsync(ContactChoiceRow? row)
    {
        if (row is null || _fromMail is null)
        {
            return;
        }

        var stored = await _contacts.GetContactAsync(row.Entry.AddressBook, row.Entry.RemoteId);
        if (stored is null)
        {
            Error = T("Kontakt nicht gefunden.");
            return;
        }

        Error = null;
        var current = ContactDraft.FromVCardWithAddresses(stored.VCardData) with
        {
            Certificates = ContactCertificates.Read(stored.VCardData),
            PgpKeys = ContactPgpKeys.Read(stored.VCardData),
        };
        Load(current.CompletedWith(_fromMail), (row.Entry.AddressBook, row.Entry.RemoteId));
        // The loaded keys are the card's: unchanged, so they are not written again.
        ExistingQuery = string.Empty;
        OnPropertyChanged(nameof(CreatesNew));
    }

    /// <summary>Back to a new contact with the sender's data.</summary>
    [RelayCommand]
    private void ChooseNew()
    {
        if (_fromMail is null)
        {
            return;
        }

        Load(_fromMail, null);
        OnPropertyChanged(nameof(CreatesNew));
    }

    /// <summary>Opened from a mail: which fields the signature filled in.</summary>
    public void DescribeSignature(ContactDraft found)
    {
        ArgumentNullException.ThrowIfNull(found);
        var what = new[]
        {
            found.Phones.Count > 0 ? T("Telefon") : null,
            found.Addresses is { Count: > 0 } ? T("Adresse") : null,
            found.Websites is { Count: > 0 } ? T("Webseite") : null,
            !string.IsNullOrEmpty(found.Organization) ? T("Firma") : null,
            !string.IsNullOrEmpty(found.Title) ? T("Funktion") : null,
        }.OfType<string>().ToList();
        SignatureText = what.Count == 0 ? null : F("Aus der Signatur der E-Mail übernommen: {0}. Bitte vor dem Speichern prüfen.", string.Join(", ", what));
        OnPropertyChanged(nameof(SignatureText));
        OnPropertyChanged(nameof(HasSignatureText));
    }

    [RelayCommand]
    private void AddAddress()
    {
        Addresses.Add(new EditableAddress(new ContactAddress(null, null, null, null, "work")));
        OnPropertyChanged(nameof(HasAddresses));
    }

    [RelayCommand]
    private void RemoveAddress(EditableAddress? address)
    {
        if (address is not null && Addresses.Remove(address))
        {
            OnPropertyChanged(nameof(HasAddresses));
        }
    }

    [RelayCommand]
    private void AddWebsite() => Websites.Add(new EditableField(FieldKind.AddressKinds, string.Empty, null));

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
        Websites.Remove(field);
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
            PgpKeys = _pgpKeysChanged ? _pgpKeyData : null,
        };

        // Written only when changed here: otherwise the card's lines stay as they are (also details not shown here).
        var addresses = Addresses.Select(a => a.ToAddress()).Where(a => !a.IsEmpty).ToList();
        var websites = Websites.Select(w => w.ToField()).Where(w => w.Value.Length > 0).ToList();
        if (_showsAddresses && !addresses.SequenceEqual(_loadedAddresses))
        {
            draft = draft with { Addresses = addresses };
        }

        if (_showsAddresses && !websites.SequenceEqual(_loadedWebsites))
        {
            draft = draft with { Websites = websites };
        }

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

        var name = $"{GivenName} {FamilyName}".Trim() is { Length: > 0 } person ? person
            : !string.IsNullOrWhiteSpace(Organization) ? Organization.Trim() : T("Kontakt");
        if (!await Infrastructure.DeleteConfirmation.AskAsync(Infrastructure.DeleteKind.Contact, F("«{0}» löschen", name),
                T("Der Kontakt wird auch auf dem Server gelöscht und lässt sich nicht wiederherstellen.")))
        {
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
