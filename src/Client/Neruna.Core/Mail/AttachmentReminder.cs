using System.Text.Json;
using System.Text.RegularExpressions;

namespace Neruna.Core.Mail;

/// <summary>
/// Einstellungen → Schreiben → Anhang-Erinnerung: words in the own text that announce an attachment ("Anhang",
/// "anbei", ".pdf" …). While one is there and nothing is attached, the compose form says so and asks before sending.
/// </summary>
public sealed class AttachmentReminderService(ISettingsStore settings)
{
    /// <summary>"false": no reminder (on unless switched off).</summary>
    public const string EnabledKey = "compose.attachmentReminder";

    /// <summary>The words (JSON list); none stored: <see cref="DefaultKeywords"/>.</summary>
    public const string KeywordsKey = "compose.attachmentKeywords";

    /// <summary>German first, then the other languages of the app – mails are often written in another one.</summary>
    public static IReadOnlyList<string> DefaultKeywords { get; } =
    [
        "Anhang", "Anhänge", "angehängt", "anbei", "beigefügt", "beiliegend", "Beilage", "Begleitschreiben",
        "attachment", "attached", "enclosed",
        "pièce jointe", "pièces jointes", "ci-joint",
        "allegato", "allegati",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".zip",
    ];

    public async Task<bool> GetEnabledAsync(CancellationToken cancellationToken = default) =>
        await settings.GetAsync(EnabledKey, cancellationToken) != "false";

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        settings.SetAsync(EnabledKey, enabled ? "true" : "false", cancellationToken);

    public async Task<IReadOnlyList<string>> GetKeywordsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await settings.GetAsync(KeywordsKey, cancellationToken) is { } json
                ? JsonSerializer.Deserialize<List<string>>(json) ?? [.. DefaultKeywords]
                : DefaultKeywords;
        }
        catch (JsonException)
        {
            return DefaultKeywords;
        }
    }

    /// <summary>Stores the words (trimmed, each once); null goes back to the defaults.</summary>
    public Task SetKeywordsAsync(IEnumerable<string>? keywords, CancellationToken cancellationToken = default) =>
        settings.SetAsync(KeywordsKey, keywords is null ? null : JsonSerializer.Serialize(Clean(keywords)), cancellationToken);

    /// <summary>The words to look for while the reminder is on; empty when it is off.</summary>
    public async Task<IReadOnlyList<string>> GetActiveKeywordsAsync(CancellationToken cancellationToken = default) =>
        await GetEnabledAsync(cancellationToken) ? await GetKeywordsAsync(cancellationToken) : [];

    /// <summary>Trimmed, without empty ones, each once (ignoring case), in the given order.</summary>
    public static List<string> Clean(IEnumerable<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        return [.. keywords.Select(k => Regex.Replace(k ?? string.Empty, @"\s+", " ").Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The first word of <paramref name="keywords"/> in <paramref name="text"/>, as written there, or null. A word counts
    /// at the start of a word only ("Anhang" in "Anhang," and "Beilage" in "Beilagen", but not in "Zusammenhang"); a
    /// file ending (".pdf") counts after a name ("Offerte.pdf") but not inside a longer one (".pdfx"). Case does not
    /// matter, spaces in a phrase match any whitespace (also a line break).
    /// </summary>
    public static string? Find(string? text, IEnumerable<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match? first = null;
        foreach (var keyword in Clean(keywords))
        {
            var match = Regex.Match(text, Pattern(keyword), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (match.Success && (first is null || match.Index < first.Index))
            {
                first = match;
            }
        }

        return first?.Value;
    }

    /// <summary>
    /// The user's own lines of a plain-text mail: without quoted lines ("> …"), and up to the signature ("-- ") or the
    /// original of a reply or forward ("-----Ursprüngliche Nachricht-----", in any language).
    /// </summary>
    public static string OwnPlainText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var own = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (line == "-- " || (trimmed.Length > 10 && trimmed.StartsWith("-----", StringComparison.Ordinal) && trimmed.EndsWith("-----", StringComparison.Ordinal)))
            {
                break;
            }

            if (!line.TrimStart().StartsWith('>'))
            {
                own.Add(line);
            }
        }

        return string.Join('\n', own);
    }

    private static string Pattern(string keyword)
    {
        var words = string.Join(@"\s+", keyword.Split(' ').Select(Regex.Escape));
        return char.IsLetterOrDigit(keyword[0])
            ? @"(?<![\p{L}\p{N}])" + words
            : words + (char.IsLetterOrDigit(keyword[^1]) ? @"(?![\p{L}\p{N}])" : string.Empty);
    }
}
