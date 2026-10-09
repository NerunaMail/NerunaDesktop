using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using Neruna.Core.Mail;
using Neruna.Core.Security;
using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

internal enum SecurityLevel
{
    None,
    Good,
    Warning,
    Bad,
}

/// <summary>
/// The right-hand pane: header, S/MIME status, attachments and the sanitized HTML body (see <see cref="MessageContent"/>).
/// Inline images come from the message itself; remote images only after the user allowed them.
/// </summary>
internal sealed partial class ReadingPaneViewModel : ViewModelBase
{
    /// <summary>The bar for an invitation, answer or cancellation in this mail (set once it is checked against the calendar).</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial InvitationBannerViewModel? Invitation { get; set; }

    private readonly MessageContent? _content;
    private readonly HttpClient? _http;
    private readonly Action? _loadRemoteContent;
    private readonly ConcurrentDictionary<string, Bitmap?> _images = new(StringComparer.Ordinal);

    private ReadingPaneViewModel(
        string subject,
        string from,
        string to,
        string cc,
        string date,
        string? info,
        MimeMessage? message,
        MessageContent? content,
        IReadOnlyList<AttachmentViewModel> attachments,
        (SecurityLevel Level, string Text, string? Detail) security,
        HttpClient? http,
        Action? loadRemoteContent,
        bool allowsRemoteContent,
        bool isInfo = false)
    {
        IsInfo = isInfo;
        Subject = subject;
        From = from;
        To = to;
        Cc = cc;
        Date = date;
        InfoText = info;
        Message = message;
        _content = content;
        Attachments = attachments;
        SecurityLevel = security.Level;
        SecurityText = security.Text;
        SecurityDetail = security.Detail;
        _http = http;
        _loadRemoteContent = loadRemoteContent;
        AllowsRemoteContent = allowsRemoteContent;
    }

    public string Subject { get; }

    public string From { get; }

    public string To { get; }

    public string Cc { get; }

    public bool HasCc => Cc.Length > 0;

    public string Date { get; }

    /// <summary>Set for placeholder panes ("Wird geladen …", errors).</summary>
    public string? InfoText { get; }

    public bool IsInfo { get; }

    public bool IsMessage => !IsInfo;

    public bool IsLoading => Message is null && !IsInfo;

    /// <summary>The readable (decrypted) message, used for reply/forward. Null while loading.</summary>
    public MimeMessage? Message { get; }

    public string Html => _content?.Html ?? string.Empty;

    public bool HasBody => _content is not null;

    public IReadOnlyList<AttachmentViewModel> Attachments { get; }

    public bool HasAttachments => Attachments.Count > 0;

    public bool AllowsRemoteContent { get; }

    public bool HasBlockedRemoteContent => _content?.HasBlockedRemoteContent ?? false;

    public SecurityLevel SecurityLevel { get; }

    public string SecurityText { get; }

    public string? SecurityDetail { get; }

    public bool HasSecurity => SecurityLevel != SecurityLevel.None;

    public bool HasSecurityDetail => !string.IsNullOrEmpty(SecurityDetail);

    public bool IsSecurityGood => SecurityLevel == SecurityLevel.Good;

    public bool IsSecurityWarning => SecurityLevel == SecurityLevel.Warning;

    public bool IsSecurityBad => SecurityLevel == SecurityLevel.Bad;

    // From the name only ("Green <servicedesk@green.ch>" → "G", not "G<"); without a name from the address.
    public string Initials
    {
        get
        {
            var name = From.Split('<')[0].Trim();
            if (name.Length == 0)
            {
                name = From.Trim('<', '>', ' ');
            }

            var initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.FirstOrDefault(char.IsLetterOrDigit)).Where(c => c != default).Take(2).Select(char.ToUpperInvariant));
            return initials.Length == 0 ? "?" : initials;
        }
    }

    public static ReadingPaneViewModel Info(string text) =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, text, null, null, [], (SecurityLevel.None, string.Empty, null), null, null, false, isInfo: true);

    public static ReadingPaneViewModel Loading(MessageSummary summary) =>
        new(summary.Subject, summary.From?.DisplayText ?? string.Empty, string.Join("; ", summary.To.Select(a => a.DisplayText)), string.Empty,
            FormatDate(summary.Date), T("Wird geladen …"), null, null, [], (SecurityLevel.None, string.Empty, null), null, null, false);

    public static ReadingPaneViewModel ForMessage(
        MessageSummary summary,
        OpenedMessage opened,
        bool allowRemoteContent,
        IFileService files,
        HttpClient http,
        Action loadRemoteContent)
    {
        var message = opened.Readable;
        var content = MessageContent.From(message, allowRemoteContent);
        return new ReadingPaneViewModel(
            message.Subject ?? summary.Subject,
            message.From.Mailboxes.FirstOrDefault() is { } from ? (string.IsNullOrWhiteSpace(from.Name) ? from.Address : $"{from.Name} <{from.Address}>") : string.Empty,
            Join(message.To.Mailboxes),
            Join(message.Cc.Mailboxes),
            FormatDate(message.Date == DateTimeOffset.MinValue ? summary.Date : message.Date),
            null,
            message,
            content,
            content.Attachments.Select(a => new AttachmentViewModel(a, files)).ToList(),
            Describe(opened.Security),
            http,
            loadRemoteContent,
            allowRemoteContent);
    }

    /// <summary>Called by the HTML view for every image; returns null for blocked or unreadable images.</summary>
    public async Task<Bitmap?> LoadImageAsync(string source)
    {
        if (_images.TryGetValue(source, out var cached))
        {
            return cached;
        }

        Bitmap? bitmap = null;
        try
        {
            if (MessageContent.ContentIdOf(source) is { } id && _content?.InlineParts.TryGetValue(id, out var part) == true && part.Content is { } inline)
            {
                using var buffer = new MemoryStream();
                await inline.DecodeToAsync(buffer);
                buffer.Position = 0;
                bitmap = new Bitmap(buffer);
            }
            else if (AllowsRemoteContent && _http is not null && Uri.TryCreate(source, UriKind.Absolute, out var url) && url.Scheme is "https" or "http")
            {
                using var response = await _http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    buffer.Position = 0;
                    bitmap = new Bitmap(buffer);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException or InvalidOperationException or NotSupportedException or TaskCanceledException)
        {
            // Unsupported format or unreachable host: show no image rather than failing the whole mail.
        }

        _images[source] = bitmap;
        return bitmap;
    }

    [RelayCommand]
    private void LoadRemoteContent() => _loadRemoteContent?.Invoke();

    private static (SecurityLevel Level, string Text, string? Detail) Describe(MessageSecurityInfo security)
    {
        if (security.DecryptionError is { } error)
        {
            return (SecurityLevel.Bad, T("Verschlüsselte Nachricht – kann nicht gelesen werden"), error);
        }

        var encrypted = security.WasEncrypted ? T(" · Verschlüsselt") : string.Empty;
        if (security.Signatures.FirstOrDefault() is not { } signature)
        {
            return security.WasEncrypted ? (SecurityLevel.Good, T("Verschlüsselt (S/MIME)"), null) : (SecurityLevel.None, string.Empty, null);
        }

        var signer = signature.SignerEmails.Count > 0 ? $"{signature.SignerName} <{signature.SignerEmails[0]}>" : signature.SignerName;
        return signature.Status switch
        {
            SignatureStatus.Valid when signature.EmailMatchesSender =>
                (SecurityLevel.Good, F("Digital signiert von {0} · Signatur gültig{1}", signer, encrypted), F("Ausgestellt von {0}", signature.IssuerName)),
            SignatureStatus.Valid =>
                (SecurityLevel.Warning, F("Signatur gültig, aber das Zertifikat ({0}) gehört nicht zur Absenderadresse{1}", signer, encrypted), null),
            SignatureStatus.ValidUntrusted =>
                (SecurityLevel.Warning, F("Signiert von {0} · Aussteller «{1}» ist nicht vertrauenswürdig{2}", signer, signature.IssuerName, encrypted),
                    T("Die Nachricht ist unverändert. Um dem Aussteller zu vertrauen, dessen Stammzertifikat unter Einstellungen → Zertifikate importieren.")),
            _ => (SecurityLevel.Bad, T("Ungültige Signatur – die Nachricht wurde nach dem Signieren verändert oder die Signatur ist beschädigt"), signature.Detail),
        };
    }

    private static string Join(IEnumerable<MailboxAddress> addresses) =>
        string.Join("; ", addresses.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : m.Name));

    private static string FormatDate(DateTimeOffset date) => date.LocalDateTime.ToString("dddd, d. MMMM yyyy HH:mm", CultureInfo.CurrentCulture);
}

internal sealed partial class AttachmentViewModel(MimeEntity entity, IFileService files) : ObservableObject
{
    public string FileName { get; } = MessageContent.FileNameOf(entity);

    public string SizeText
    {
        get
        {
            if (entity is not MimePart { Content: { Stream: { CanSeek: true } stream } content })
            {
                return string.Empty;
            }

            // Base64 inflates by 4/3; good enough for display.
            var bytes = content.Encoding == ContentEncoding.Base64 ? stream.Length * 3 / 4 : stream.Length;
            return bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024} KB",
                _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
            };
        }
    }

    [RelayCommand]
    private Task OpenAsync() => files.OpenAsync(FileName, WriteToAsync);

    [RelayCommand]
    private async Task SaveAsync()
    {
        var target = await files.SaveFileAsync(T("Anhang speichern"), FileName);
        if (target is not null)
        {
            await using (target)
            {
                await WriteToAsync(target);
            }
        }
    }

    private Task WriteToAsync(Stream target) => entity switch
    {
        MimePart { Content: { } content } => content.DecodeToAsync(target),
        MessagePart { Message: { } message } => message.WriteToAsync(target),
        _ => entity.WriteToAsync(target),
    };
}
