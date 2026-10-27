using System.Globalization;
using System.Text;
using System.Text.Json;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Mail;

/// <summary>
/// A tag ("Schlagwort") for mails: name and colour. <see cref="Keyword"/> is what is stored on the mail server – an
/// IMAP keyword (like other mail programs: "$label1" is "Wichtig"); Microsoft 365 and Exchange store the name as category.
/// </summary>
/// <param name="Color">#rrggbb</param>
/// <param name="Source">The organisation it comes from (Neruna Cloud, not editable here); null: the user's own.</param>
public sealed record MailTag(string Keyword, string Name, string Color, string? Source = null)
{
    public bool IsFromCloud => Source is not null;

    /// <summary>A keyword or category on a message means this tag (by keyword, or by name for categories).</summary>
    public bool Matches(string keyword) =>
        string.Equals(keyword, Keyword, StringComparison.OrdinalIgnoreCase) || string.Equals(keyword, Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An IMAP keyword (atom) from the name, as Neruna Cloud makes it: lower-case, accents dropped, other characters
    /// "_" ("Persönlich & dringend" → "personlich_dringend"); a taken one gets "_2", "_3" …
    /// </summary>
    public static string KeywordFor(string name, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var c in name.Replace("ß", "ss", StringComparison.Ordinal).Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? c : '_');
        }

        var collapsed = System.Text.RegularExpressions.Regex.Replace(builder.ToString(), "_+", "_").Trim('_', '-');
        var stem = collapsed.Length == 0 ? "tag" : collapsed[..Math.Min(collapsed.Length, 50)];
        var keyword = stem;
        for (var i = 2; used.Contains(keyword); i++)
        {
            keyword = $"{stem}_{i}";
        }

        return keyword;
    }
}

/// <summary>
/// The tags offered on mails: the organisations' (Neruna Cloud) first, then the user's own. Until the user changes
/// them, the own ones are the five that other mail programs use too ("$label1" … "$label5").
/// </summary>
public sealed class MailTagService(ISettingsStore settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public event EventHandler? Changed;

    public static IReadOnlyList<MailTag> Defaults =>
    [
        new("$label1", T("Wichtig"), "#ff0000"),
        new("$label2", T("Arbeit"), "#ff9900"),
        new("$label3", T("Persönlich"), "#009900"),
        new("$label4", T("Zu erledigen"), "#3333ff"),
        new("$label5", T("Später"), "#993399"),
    ];

    public async Task<IReadOnlyList<MailTag>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var cloud = (await GetCloudAsync(cancellationToken)).Values.SelectMany(t => t).ToList();
        var own = await GetOwnAsync(cancellationToken);
        return [.. cloud.DistinctBy(t => t.Keyword, StringComparer.OrdinalIgnoreCase),
            .. own.Where(o => !cloud.Any(c => string.Equals(c.Keyword, o.Keyword, StringComparison.OrdinalIgnoreCase)))];
    }

    public async Task<IReadOnlyList<MailTag>> GetOwnAsync(CancellationToken cancellationToken = default) =>
        Read<List<MailTag>>(await settings.GetAsync(SettingKeys.MailTags, cancellationToken))?.Select(t => t with { Source = null }).ToList()
        ?? [.. Defaults];

    public async Task SaveOwnAsync(IReadOnlyList<MailTag> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        await settings.SetAsync(SettingKeys.MailTags, JsonSerializer.Serialize(tags.Select(t => t with { Source = null }), Json), cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The tags of one organisation (replacing what it had); empty removes them.</summary>
    /// <returns>Something changed.</returns>
    public async Task<bool> SetCloudAsync(string source, IReadOnlyList<MailTag> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var all = await GetCloudAsync(cancellationToken);
        var offered = tags.Select(t => t with { Source = source }).ToList();
        if (all.TryGetValue(source, out var current) ? current.SequenceEqual(offered) : offered.Count == 0)
        {
            return false;
        }

        if (offered.Count == 0)
        {
            all.Remove(source);
        }
        else
        {
            all[source] = offered;
        }

        await settings.SetAsync(SettingKeys.MailTagsCloud, all.Count == 0 ? null : JsonSerializer.Serialize(all, Json), cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<IReadOnlyCollection<string>> GetCloudSourcesAsync(CancellationToken cancellationToken = default) =>
        (await GetCloudAsync(cancellationToken)).Keys;

    private async Task<Dictionary<string, List<MailTag>>> GetCloudAsync(CancellationToken cancellationToken) =>
        Read<Dictionary<string, List<MailTag>>>(await settings.GetAsync(SettingKeys.MailTagsCloud, cancellationToken)) ?? [];

    private static T? Read<T>(string? json) where T : class
    {
        try
        {
            return json is null ? null : JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
