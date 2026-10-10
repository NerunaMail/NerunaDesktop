using System.Globalization;
using System.Text.RegularExpressions;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Mail;

/// <summary>
/// The text of the out-of-office reply with placeholders: {{start}} and {{end}} become the first and the last day
/// away when it is switched on. The server only knows the finished text, so Neruna keeps the template per account and
/// uses it again as long as the server still has what Neruna wrote (a text changed in the webmail wins).
/// </summary>
public static partial class AutoReplyText
{
    [GeneratedRegex(@"\{\{\s*(start|end)\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    public static bool HasPlaceholders(string template) => Placeholder().IsMatch(template ?? string.Empty);

    /// <summary>The text with the dates in: {{start}} the first day, {{end}} the last day away (the end is exclusive).</summary>
    /// <remarks>Without a period the placeholders stay as they are.</remarks>
    public static string Render(string template, DateTimeOffset? start, DateTimeOffset? end, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (start is null || end is null)
        {
            return template;
        }

        culture ??= Culture;
        return Placeholder().Replace(template, m => m.Groups[1].Value.Equals("start", StringComparison.OrdinalIgnoreCase)
            ? start.Value.LocalDateTime.Date.ToString("D", culture)
            : end.Value.LocalDateTime.Date.AddDays(-1).ToString("D", culture));
    }

    /// <summary>The template to edit: Neruna's own if the server still has what it made of it, else the server's text.</summary>
    public static async Task<string> TemplateForAsync(ISettingsStore settings, Guid connectionId, AutoReply onServer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(onServer);
        var own = await settings.GetAsync(Key(connectionId), cancellationToken);
        return own is not null && Normalize(Render(own, onServer.Start, onServer.End)) == Normalize(onServer.Message) ? own : onServer.Message;
    }

    public static Task RememberAsync(ISettingsStore settings, Guid connectionId, string template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.SetAsync(Key(connectionId), template, cancellationToken);
    }

    /// <summary>The text Neruna suggests the first time.</summary>
    public static string Default(string name) =>
        F("Guten Tag\n\nIch bin zurzeit nicht im Büro und lese meine E-Mails nicht regelmässig. Ihre Nachricht wird nach meiner Rückkehr bearbeitet.\n\nFreundliche Grüsse\n{0}", name);

    /// <summary>The message to store: with the dates in when there is a period.</summary>
    /// <exception cref="ArgumentException">Switched on with {{start}}/{{end}} but without a period (the message says so, for the user).</exception>
    public static string ForServer(string template, bool enabled, DateTimeOffset? start, DateTimeOffset? end)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (enabled && (start is null || end is null) && HasPlaceholders(template))
        {
            throw new ArgumentException(T("Der Text enthält {{start}} oder {{end}} – dafür bitte einen Zeitraum wählen."), nameof(template));
        }

        return Render(template.Trim(), start, end);
    }

    private const string TemplatesKey = "autoreply.templates";

    /// <summary>The named texts ("Ferien", "Bei Kunden" …), for all accounts, kept in the settings (so also in a backup).</summary>
    public static async Task<IReadOnlyList<AutoReplyTemplate>> LoadTemplatesAsync(ISettingsStore settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (await settings.GetAsync(TemplatesKey, cancellationToken) is not { Length: > 0 } json)
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<AutoReplyTemplate>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Adds the template or replaces the one with the same name (ignoring case).</summary>
    public static async Task<IReadOnlyList<AutoReplyTemplate>> SaveTemplateAsync(ISettingsStore settings, AutoReplyTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        var list = (await LoadTemplatesAsync(settings, cancellationToken)).Where(t => !t.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase)).Append(template)
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        await settings.SetAsync(TemplatesKey, System.Text.Json.JsonSerializer.Serialize(list), cancellationToken);
        return list;
    }

    public static async Task<IReadOnlyList<AutoReplyTemplate>> DeleteTemplateAsync(ISettingsStore settings, string name, CancellationToken cancellationToken = default)
    {
        var list = (await LoadTemplatesAsync(settings, cancellationToken)).Where(t => !t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        await settings.SetAsync(TemplatesKey, System.Text.Json.JsonSerializer.Serialize(list), cancellationToken);
        return list;
    }

    private static string Key(Guid connectionId) => $"autoreply.template.{connectionId:N}";

    // Servers change line ends and trailing spaces.
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}

/// <summary>A named out-of-office text, e.g. "Ferien" or "Bei Kunden"; may use {{start}} and {{end}}.</summary>
public sealed record AutoReplyTemplate(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text,
    [property: System.Text.Json.Serialization.JsonPropertyName("subject")] string? Subject = null);
