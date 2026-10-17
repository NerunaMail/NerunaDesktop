using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using Neruna.Core.Mail;
using Neruna.Core.Privacy;
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

/// <summary>How a mail is shown this time: the privacy settings, the phishing check, pictures, original layout.</summary>
internal sealed record MessageView(PrivacyOptions Privacy, ThreatAssessment? Assessment, bool AllowRemoteContent = false, bool ShowOriginal = false)
{
    /// <summary>Without settings (attached mails before their own check): as set by default, pictures on request.</summary>
    public static MessageView Default { get; } = new(new PrivacyOptions(), null);
}

/// <summary>One line of the security check, for the list.</summary>
internal sealed record FindingItem(Finding Finding)
{
    public string Title => Finding.Title;

    public string? Detail => Finding.Detail;

    public bool HasDetail => !string.IsNullOrEmpty(Finding.Detail);

    public bool IsGood => Finding.Severity == Severity.Good;

    public bool IsInfo => Finding.Severity == Severity.Info;

    public bool IsLow => Finding.Severity == Severity.Low;

    public bool IsMedium => Finding.Severity == Severity.Medium;

    public bool IsHigh => Finding.Severity == Severity.High;

    public string Mark => Finding.Severity switch
    {
        Severity.Good => "✓",
        Severity.Info => "i",
        Severity.Low => "!",
        _ => "⚠",
    };
}

/// <summary>A link of the mail with something to note: what it shows, where it really goes.</summary>
internal sealed record CheckedLinkItem(LinkCheck Check)
{
    public string Shown => string.IsNullOrWhiteSpace(Check.Text) ? Check.Href : Check.Text!;

    public string Goes => Check.Host ?? Check.Href;

    public string Href => Check.Href;

    public bool IsSuspicious => Check.IsSuspicious;
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
        foreach (var attachment in attachments)
        {
            attachment.TogglePreview = TogglePreview;
        }
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

    /// <summary>The message as text (for the AI summary).</summary>
    public string PlainText => _content?.PlainText ?? string.Empty;

    /// <summary>The AI summary shown above the message, if asked for.</summary>
    [ObservableProperty]
    public partial AiSummaryViewModel? Summary { get; set; }

    public bool HasBody => _content is not null;

    /// <summary>The message text, unless an attachment is shown in its place.</summary>
    public bool ShowsBody => HasBody && Preview is null;

    /// <summary>The image or PDF attachment shown instead of the message text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBody))]
    public partial AttachmentPreviewViewModel? Preview { get; set; }

    /// <summary>Set on the mail page: PDFs can be stamped, and answered or forwarded stamped.</summary>
    public IAttachmentActions? AttachmentActions { get; set; }

    private void TogglePreview(AttachmentViewModel attachment)
    {
        var showing = Preview?.Attachment;
        ClosePreview();
        if (showing == attachment)
        {
            return;
        }

        attachment.IsPreviewed = true;
        Preview = new AttachmentPreviewViewModel(attachment, ClosePreview, AttachmentActions, showMessage: m => Attached(m, allowRemoteContent: false, attachment.Files));
        _ = Preview.LoadAsync();
    }

    /// <summary>A quick action's stamp step: the PDF in the preview, ready for the stamp; the stamped PDF or null (cancelled).</summary>
    public async Task<byte[]?> StampAsync(AttachmentViewModel attachment, PdfStamp stamp, byte[]? content)
    {
        ClosePreview();
        attachment.IsPreviewed = true;
        var preview = new AttachmentPreviewViewModel(attachment, ClosePreview, AttachmentActions, content);
        Preview = preview;
        await preview.LoadAsync();
        var stamped = await preview.StampForActionAsync(stamp);
        return stamped;
    }

    // An attached mail as a message of its own: pictures from the internet only on request, as for any mail.
    private ReadingPaneViewModel Attached(MimeMessage message, bool allowRemoteContent, IFileService files) =>
        Attached(message, View with { Assessment = null, AllowRemoteContent = allowRemoteContent, ShowOriginal = false }, files);

    private ReadingPaneViewModel Attached(MimeMessage message, MessageView view, IFileService files)
    {
        var summary = new MessageSummary(string.Empty, message.MessageId, message.InReplyTo, message.Subject ?? string.Empty, null, [],
            message.Date, MessageFlags.Seen, 0, false, null);
        ReadingPaneViewModel? pane = null;
        pane = ForMessage(summary, new OpenedMessage(message, MessageSecurityInfo.None), view, files, _http ?? new HttpClient(), next =>
        {
            if (Preview is { } preview && preview.Message == pane)
            {
                preview.Message = Attached(message, next, files);
            }
        }, hideSpamNote: HideSpamNoteAction);
        return pane;
    }

    private void ClosePreview()
    {
        if (Preview is { } preview)
        {
            preview.Attachment.IsPreviewed = false;
            Preview = null;
        }
    }

    public IReadOnlyList<AttachmentViewModel> Attachments { get; }

    public bool HasAttachments => Attachments.Count > 0;

    public bool AllowsRemoteContent { get; }

    public bool HasBlockedRemoteContent => _content?.HasBlockedRemoteContent ?? false;

    /// <summary>How this mail is shown (privacy settings, check, pictures, layout).</summary>
    public MessageView View { get; private init; } = MessageView.Default;

    /// <summary>Shows the mail again with another view (pictures loaded, original layout).</summary>
    private Action<MessageView>? Reshow { get; init; }

    /// <summary>Adds a sender or domain to the trusted ones (pictures without asking).</summary>
    private Func<string, Task>? Trust { get; init; }

    /// <summary>Keeps "Ausblenden" of the spam note for all mails.</summary>
    private Func<Task>? HideSpamNoteAction { get; init; }

    /// <summary>The note "kein Spamfilter" in the check; hidden, an "i" next to the title shows it again.</summary>
    [ObservableProperty]
    public partial bool IsSpamNoteShown { get; set; } = true;

    [RelayCommand]
    private async Task HideSpamNoteAsync()
    {
        IsSpamNoteShown = false;
        if (HideSpamNoteAction is { } hide)
        {
            await hide();
        }
    }

    [RelayCommand]
    private void ShowSpamNote() => IsSpamNoteShown = !IsSpamNoteShown;

    /// <summary>"Kopfzeilen anzeigen": every header of the mail as it arrived, for the curious and for support.</summary>
    [ObservableProperty]
    public partial bool IsHeadersOpen { get; set; }

    public string HeadersText => Message is { } message
        ? string.Join("\n", message.Headers.Select(h => h.Field + ":" + System.Text.Encoding.UTF8.GetString(h.RawValue).TrimEnd('\r', '\n')))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
        : string.Empty;

    public bool HasHeaders => Message is not null;

    [RelayCommand]
    private void ToggleHeaders() => IsHeadersOpen = !IsHeadersOpen;

    /// <summary>"Bilder laden" is offered (not when pictures are blocked in the settings).</summary>
    public bool CanLoadRemoteContent => View.Privacy.RemoteContent != RemoteContentPolicy.Block;

    public string RemoteContentText => CanLoadRemoteContent
        ? T("Externe Bilder wurden zum Schutz Ihrer Privatsphäre nicht geladen.")
        : T("Externe Bilder sind in den Datenschutz-Einstellungen gesperrt.");

    private string? SenderAddress => Message?.From.Mailboxes.FirstOrDefault()?.Address;

    /// <summary>"Immer für diesen Absender": not for a sender that failed the check (the address may be forged).</summary>
    public bool CanTrustSender => CanLoadRemoteContent && Trust is not null && SenderAddress is not null && View.Assessment?.SenderForged != true;

    public string TrustSenderText => F("Immer von {0}", SenderAddress);

    public string TrustDomainText => F("Immer von @{0}", DomainNames.OfAddress(SenderAddress));

    /// <summary>Tracking pixels found in the mail; they stay out (also when pictures are loaded, as set).</summary>
    public int Trackers => _content?.Trackers ?? 0;

    public bool HasTrackers => Trackers > 0 && (View.Privacy.BlockTrackers || !AllowsRemoteContent);

    /// <summary>Pictures are shown, but tracking pixels were kept out: a small note instead of the bar.</summary>
    public bool ShowsTrackersOnly => !HasBlockedRemoteContent && AllowsRemoteContent && Trackers > 0 && View.Privacy.BlockTrackers;

    public string TrackersText => Trackers == 1 ? T("1 Tracking-Pixel blockiert") : F("{0} Tracking-Pixel blockiert", Trackers);

    /// <summary>Shown as text only (setting "Nur Text"); the original on request.</summary>
    public bool ShowsPlainTextOnly => _content?.ShowsPlainTextOnly ?? false;

    [RelayCommand]
    private void ShowOriginal() => Reshow?.Invoke(View with { ShowOriginal = true });

    [RelayCommand]
    private async Task TrustSenderAsync()
    {
        if (SenderAddress is { } address && Trust is { } trust)
        {
            await trust(address);
            Reshow?.Invoke(View with { AllowRemoteContent = true });
        }
    }

    [RelayCommand]
    private async Task TrustDomainAsync()
    {
        if (DomainNames.OfAddress(SenderAddress) is { } domain && Trust is { } trust)
        {
            await trust("@" + domain);
            Reshow?.Invoke(View with { AllowRemoteContent = true });
        }
    }

    // The security check: computed when the mail was opened, or here for an attached mail.
    private ThreatAssessment? _assessment;

    private ThreatAssessment? Assessment => _assessment ??= View.Assessment ?? (Message is { } m ? MessageAnalyzer.Analyze(m) : null);

    /// <summary>The warning above a suspicious mail (setting "Bei verdächtigen Mails warnen").</summary>
    public bool ShowsWarning => View.Privacy.WarnSuspicious && View.Assessment is { Level: not ThreatLevel.Unremarkable } && !IsAnalysisOpen;

    public bool IsDangerous => Assessment?.Level == ThreatLevel.Dangerous;

    public bool IsCaution => Assessment?.Level == ThreatLevel.Caution;

    public bool IsUnremarkable => Assessment?.Level == ThreatLevel.Unremarkable;

    public string AssessmentSummary => Assessment?.Summary ?? string.Empty;

    /// <summary>The panel "Sicherheitsprüfung" with every finding, the sender in its places and the noteworthy links.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsWarning))]
    public partial bool IsAnalysisOpen { get; set; }

    public IReadOnlyList<FindingItem> Findings => Assessment?.Findings.Select(f => new FindingItem(f)).ToList() ?? [];

    public IReadOnlyList<CheckedLinkItem> CheckedLinks => Assessment?.Links.Select(l => new CheckedLinkItem(l)).ToList() ?? [];

    public bool HasCheckedLinks => Assessment?.Links.Count > 0;

    public string SenderFrom => Assessment?.Sender.From ?? string.Empty;

    public string SenderEnvelope => Assessment?.Sender.Envelope ?? T("(nicht angegeben)");

    public string SenderReplyTo => Assessment?.Sender.ReplyTo ?? T("(wie Absender)");

    public string SenderAuthentication => Assessment?.Sender.Authentication ?? T("(kein verwertbares Ergebnis Ihres Servers)");

    [RelayCommand]
    private void ToggleAnalysis()
    {
        IsAnalysisOpen = !IsAnalysisOpen;
        OnPropertyChanged(nameof(Findings));
        OnPropertyChanged(nameof(CheckedLinks));
        OnPropertyChanged(nameof(HasCheckedLinks));
        OnPropertyChanged(nameof(IsDangerous));
        OnPropertyChanged(nameof(IsCaution));
        OnPropertyChanged(nameof(IsUnremarkable));
        OnPropertyChanged(nameof(AssessmentSummary));
        OnPropertyChanged(nameof(SenderFrom));
        OnPropertyChanged(nameof(SenderEnvelope));
        OnPropertyChanged(nameof(SenderReplyTo));
        OnPropertyChanged(nameof(SenderAuthentication));
    }

    /// <summary>A link in the mail was clicked: checked, shown first if the settings say so, then opened (cleaned up if set).</summary>
    public async Task OpenLinkAsync(string href, Func<LinkCheck, Task<bool>> confirm, Func<string, Task> open)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(open);
        var check = _content?.CheckLink(href, View.Privacy.CleanLinks) ?? LinkInspector.Inspect(href, clean: View.Privacy.CleanLinks);
        if (check.Target is null)
        {
            if (check.IsSuspicious)
            {
                await confirm(check);
            }

            return; // no web address: never opened from here
        }

        if (View.Privacy.Confirms(check) && !await confirm(check))
        {
            return;
        }

        await open(check.Opens);
    }

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
        MessageView view,
        IFileService files,
        HttpClient http,
        Action<MessageView> reshow,
        Func<string, Task>? trust = null,
        Func<Task>? hideSpamNote = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        var message = opened.Readable;
        var content = MessageContent.From(message, view.AllowRemoteContent, view.Privacy.BlockTrackers, view.Privacy.PlainText && !view.ShowOriginal);
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
            () => reshow(view with { AllowRemoteContent = true }),
            view.AllowRemoteContent)
        {
            View = view,
            Reshow = reshow,
            Trust = trust,
            HideSpamNoteAction = hideSpamNote,
            IsSpamNoteShown = !view.Privacy.HideSpamNote,
        };
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
                // A picture in a mail, not a download: at most 10 MB and 20 seconds, the rest of the mail stays usable.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is null or <= MaxRemoteImageBytes)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
                    {
                        if (buffer.Length + read > MaxRemoteImageBytes)
                        {
                            throw new InvalidOperationException("Remote image too large");
                        }

                        buffer.Write(chunk, 0, read);
                    }

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

    private const long MaxRemoteImageBytes = 10 * 1024 * 1024;

    [RelayCommand]
    private void LoadRemoteContent() => _loadRemoteContent?.Invoke();

    private static (SecurityLevel Level, string Text, string? Detail) Describe(MessageSecurityInfo security)
    {
        var (level, text, detail) = MessageSecurityText.Describe(security);
        return ((SecurityLevel)(int)level, text, detail);
    }

    private static string Join(IEnumerable<MailboxAddress> addresses) =>
        string.Join("; ", addresses.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : m.Name));

    private static string FormatDate(DateTimeOffset date) => date.LocalDateTime.ToString("dddd, d. MMMM yyyy HH:mm", CultureInfo.CurrentCulture);
}

internal sealed partial class AttachmentViewModel(MimeEntity entity, IFileService files) : ObservableObject
{
    /// <summary>For an attached mail's own attachments.</summary>
    public IFileService Files => files;

    public string FileName { get; } = MessageContent.FileNameOf(entity);

    /// <summary>The part in the mail (forwarding replaces it with a stamped copy).</summary>
    public MimeEntity Entity => entity;

    /// <summary>For the conditions of quick actions.</summary>
    public QuickActionAttachment Describe() => new(FileName, entity.ContentType?.MimeType ?? string.Empty);

    /// <summary>An attachment part with other content (the stamped PDF), same name and type.</summary>
    public MimePart WithContent(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new MimePart(entity.ContentType?.MimeType ?? "application/octet-stream")
        {
            Content = new MimeContent(new MemoryStream(content)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = FileName,
        };
    }

    /// <summary>Saves other content (the stamped PDF) under the attachment's name, or <paramref name="suggestedName"/> in <paramref name="folder"/>.</summary>
    public async Task<bool> SaveContentAsync(byte[] content, string? suggestedName = null, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var target = await files.SaveFileAsync(T("Anhang speichern"), suggestedName ?? FileName, folder);
        if (target is null)
        {
            return false;
        }

        await using (target)
        {
            await target.WriteAsync(content);
        }

        return true;
    }

    public AttachmentPreviewKind PreviewKind => AttachmentPreview.KindOf(entity, FileName);

    public bool CanPreview => PreviewKind != AttachmentPreviewKind.None;

    public string ActivateTip => CanPreview ? T("Vorschau im Lesebereich") : T("Öffnen");

    /// <summary>Shown in the reading pane right now (highlighted).</summary>
    [ObservableProperty]
    public partial bool IsPreviewed { get; set; }

    /// <summary>Set by the reading pane: shows this attachment in place of the message, or the message again.</summary>
    internal Action<AttachmentViewModel>? TogglePreview { get; set; }

    /// <summary>A click on the name: images and PDFs in the reading pane, everything else in its program.</summary>
    [RelayCommand]
    private Task ActivateAsync()
    {
        if (CanPreview && TogglePreview is { } toggle)
        {
            toggle(this);
            return Task.CompletedTask;
        }

        return OpenAsync();
    }

    /// <summary>The decoded content for the preview; null when it is larger than <see cref="AttachmentPreview.MaxBytes"/>.</summary>
    public async Task<byte[]?> ReadAsync()
    {
        using var buffer = new MemoryStream();
        await WriteToAsync(buffer);
        return buffer.Length > AttachmentPreview.MaxBytes ? null : buffer.ToArray();
    }

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
    private async Task OpenAsync() => await files.OpenAsync(FileName, WriteToAsync);

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
