using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Mail;

/// <summary>A stamp to set on a PDF ("Genehmigt", name, date); the user's own list, four to start with.</summary>
/// <param name="Color">"#RRGGBB".</param>
public sealed record PdfStamp(Guid Id, string Text, string Color, bool WithName = true, bool WithDate = true)
{
    /// <summary>The lines below the stamp text: the user's name and the moment of stamping, as chosen.</summary>
    public IReadOnlyList<string> Lines(string? name, DateTime now)
    {
        var lines = new List<string>();
        if (WithName && !string.IsNullOrWhiteSpace(name))
        {
            lines.Add(name.Trim());
        }

        if (WithDate)
        {
            lines.Add(now.ToString("g", CultureInfo.CurrentCulture));
        }

        return lines;
    }
}

/// <summary>Which attachments a quick action needs (and works on).</summary>
public enum QuickActionAttachmentKind
{
    /// <summary>No condition on attachments.</summary>
    None,
    Any,
    Pdf,
    Image,
    Office,

    /// <summary>The endings in <see cref="QuickActionCondition.Extensions"/> (e.g. "xml, csv").</summary>
    Extension,
}

/// <summary>When the button of a quick action appears: all given parts must apply.</summary>
public sealed record QuickActionCondition
{
    public QuickActionAttachmentKind Attachment { get; init; }

    public string? Extensions { get; init; }

    /// <summary>A part of the sender's name or address ("@lieferant.ch").</summary>
    public string? SenderContains { get; init; }

    public string? SubjectContains { get; init; }

    public Guid? AccountId { get; init; }

    /// <summary>The folder (with <see cref="AccountId"/>); <see cref="FolderName"/> is only for showing it.</summary>
    public string? FolderRemoteId { get; init; }

    public string? FolderName { get; init; }
}

public enum QuickActionStepKind
{
    /// <summary>The PDF opens to set the stamp by hand.</summary>
    StampPdf,

    /// <summary>The save dialog opens in the given folder with the given name.</summary>
    SaveAttachment,

    /// <summary>The attachments go to the default printer.</summary>
    Print,

    /// <summary>The attachments open in their programs.</summary>
    OpenAttachment,
    Reply,
    ReplyAll,
    Forward,

    /// <summary>A new mail with the attachments, to <see cref="QuickActionStep.To"/>.</summary>
    NewMail,

    /// <summary>The mail moves to <see cref="QuickActionStep.FolderRemoteId"/> (of its own account).</summary>
    MoveToFolder,
    Flag,
    MarkRead,
}

/// <summary>One step of a quick action; only the fields of its kind are used.</summary>
public sealed record QuickActionStep(QuickActionStepKind Kind)
{
    public Guid? StampId { get; init; }

    public string? Folder { get; init; }

    /// <summary>With {Datum}, {Absender}, {Betreff}, {Dateiname}; the ending is added.</summary>
    public string? FileName { get; init; }

    /// <summary>A text template above the signature (answer and forward).</summary>
    public Guid? TemplateId { get; init; }

    /// <summary>Forward and new mail: to this address (several with ";").</summary>
    public string? To { get; init; }

    /// <summary>New mail: the subject, with the same fields as <see cref="FileName"/>.</summary>
    public string? Subject { get; init; }

    /// <summary>Move: the folder (its name is in <see cref="FolderName"/> for showing).</summary>
    public string? FolderRemoteId { get; init; }

    public string? FolderName { get; init; }
}

/// <summary>
/// "Schnellaktionen": a button over the open mail when the conditions apply; a click runs the steps one after another
/// (stamp, save, answer …) with the attachments that meet the condition.
/// </summary>
/// <param name="Color">The button's colour, "#RRGGBB".</param>
public sealed record QuickAction(Guid Id, string Label, string Color, QuickActionCondition When, IReadOnlyList<QuickActionStep> Steps);

/// <summary>An attachment of the open mail, as far as the conditions need it.</summary>
public sealed record QuickActionAttachment(string FileName, string MimeType);

/// <summary>The open mail, as far as the conditions need it.</summary>
public sealed record QuickActionMessage(Guid AccountId, string? FolderRemoteId, string From, string Subject, IReadOnlyList<QuickActionAttachment> Attachments);

/// <summary>Quick actions and stamps (kept in the settings), and whether one applies to a mail.</summary>
public sealed class QuickActionService(ISettingsStore settings)
{
    private const string ActionsKey = "mail.quickActions";
    private const string StampsKey = "pdf.stamps";
    private const string StampNameKey = "pdf.stampName";

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".csv",
    };

    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff",
    };

    /// <summary>The colours offered for buttons and stamps.</summary>
    public static IReadOnlyList<string> Colors { get; } = ["#0F6CBD", "#107C10", "#C42B1C", "#CA5010", "#8764B8", "#038387", "#C239B3", "#69797E"];

    /// <summary>Raised after the actions or the stamps were saved.</summary>
    public event EventHandler? Changed;

    /// <summary>The four to start with – fixed ids, so actions keep pointing at them.</summary>
    public static IReadOnlyList<PdfStamp> DefaultStamps =>
    [
        new(new Guid("6a0e6f3e-3c1b-4c53-9a43-0d2f6b1d0001"), T("Genehmigt"), "#107C10"),
        new(new Guid("6a0e6f3e-3c1b-4c53-9a43-0d2f6b1d0002"), T("Geprüft"), "#0F6CBD"),
        new(new Guid("6a0e6f3e-3c1b-4c53-9a43-0d2f6b1d0003"), T("Abgelehnt"), "#C42B1C"),
        new(new Guid("6a0e6f3e-3c1b-4c53-9a43-0d2f6b1d0004"), T("Erhalten"), "#8764B8"),
    ];

    public async Task<IReadOnlyList<QuickAction>> GetActionsAsync(CancellationToken cancellationToken = default) =>
        Read<List<QuickAction>>(await settings.GetAsync(ActionsKey, cancellationToken)) ?? [];

    public async Task SaveActionsAsync(IReadOnlyList<QuickAction> actions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actions);
        await settings.SetAsync(ActionsKey, actions.Count == 0 ? null : JsonSerializer.Serialize(actions, Json), cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The user's stamps; the four defaults until the list was changed once.</summary>
    public async Task<IReadOnlyList<PdfStamp>> GetStampsAsync(CancellationToken cancellationToken = default) =>
        Read<List<PdfStamp>>(await settings.GetAsync(StampsKey, cancellationToken)) ?? DefaultStamps;

    public async Task SaveStampsAsync(IReadOnlyList<PdfStamp> stamps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        await settings.SetAsync(StampsKey, JsonSerializer.Serialize(stamps, Json), cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The name on stamps when set by hand; null means the name of the person logged in to the computer.</summary>
    public Task<string?> GetStampNameAsync(CancellationToken cancellationToken = default) => settings.GetAsync(StampNameKey, cancellationToken);

    public Task SetStampNameAsync(string? name, CancellationToken cancellationToken = default) =>
        settings.SetAsync(StampNameKey, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), cancellationToken);

    private static T? Read<T>(string? json)
        where T : class
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether the button of <paramref name="action"/> appears for this mail.</summary>
    public static bool Matches(QuickAction action, QuickActionMessage message)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(message);
        var when = action.When;
        if (when.AccountId is { } account && account != message.AccountId)
        {
            return false;
        }

        if (when.AccountId is not null && !string.IsNullOrEmpty(when.FolderRemoteId) && !string.Equals(when.FolderRemoteId, message.FolderRemoteId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!Contains(message.From, when.SenderContains) || !Contains(message.Subject, when.SubjectContains))
        {
            return false;
        }

        return when.Attachment == QuickActionAttachmentKind.None || AttachmentsFor(action, message.Attachments, a => a).Count > 0;
    }

    /// <summary>The attachments the steps work on: those the condition asks for (all, without a condition on them).</summary>
    public static IReadOnlyList<T> AttachmentsFor<T>(QuickAction action, IReadOnlyList<T> attachments, Func<T, QuickActionAttachment> describe)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(describe);
        var when = action.When;
        var endings = (when.Extensions ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => "." + e.Trim().TrimStart('*').TrimStart('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. attachments.Where(item =>
        {
            var a = describe(item);
            var ending = Path.GetExtension(a.FileName);
            return when.Attachment switch
            {
                QuickActionAttachmentKind.Pdf => IsPdf(a),
                QuickActionAttachmentKind.Image => Images.Contains(ending) || a.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
                QuickActionAttachmentKind.Office => Office.Contains(ending),
                QuickActionAttachmentKind.Extension => endings.Contains(ending),
                _ => true,
            };
        })];
    }

    public static bool IsPdf(QuickActionAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return Path.GetExtension(attachment.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || attachment.MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string text, string? part) =>
        string.IsNullOrWhiteSpace(part) || text.Contains(part.Trim(), StringComparison.CurrentCultureIgnoreCase);

    /// <summary>{Datum}, {Absender}, {Betreff} in a text (the subject of a new mail).</summary>
    public static string Fill(string? pattern, string sender, string subject, DateTime date) =>
        (pattern ?? string.Empty)
            .Replace("{Datum}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{Absender}", sender, StringComparison.OrdinalIgnoreCase)
            .Replace("{Betreff}", subject, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name to suggest when saving: the pattern with {Datum} (2026-10-14), {Absender}, {Betreff} and {Dateiname}
    /// (without ending) filled in, the attachment's ending added; without a pattern the attachment's name.
    /// </summary>
    public static string FileName(string? pattern, string original, string sender, string subject, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(original);
        var ending = Path.GetExtension(original);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Clean(original);
        }

        var name = pattern.Trim()
            .Replace("{Datum}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{Absender}", sender, StringComparison.OrdinalIgnoreCase)
            .Replace("{Betreff}", subject, StringComparison.OrdinalIgnoreCase)
            .Replace("{Dateiname}", Path.GetFileNameWithoutExtension(original), StringComparison.OrdinalIgnoreCase);
        if (ending.Length > 0 && !name.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
        {
            name += ending;
        }

        return Clean(name);
    }

    // Characters no file system takes become "_"; also those Windows forbids, so a name works everywhere.
    private static string Clean(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
        return clean.Length == 0 ? "anhang" : clean;
    }
}
