namespace Neruna.Core.Mail;

/// <summary>
/// A text template ("Textvorlage"): a block of formatted text inserted at the caret when writing (a call note table,
/// a standard reply …). The user's own ones, or the organisation's from Neruna Cloud (<see cref="Source"/> "cloud").
/// </summary>
/// <param name="Shortcut">Typed with "::" while writing (e.g. "tel" → "tel::"), the template replaces it.</param>
public sealed record TextTemplate(Guid Id, string Name, string Html, DateTimeOffset UpdatedAt, string Source = Signature.LocalSource, string? Shortcut = null)
{
    public bool IsFromCloud => Source == Signature.CloudSource;

    /// <summary>Letters, digits, - and _; stored lower-case, empty is none.</summary>
    public static string? NormalizeShortcut(string? shortcut)
    {
        var value = new string((shortcut ?? string.Empty).Trim().TrimEnd(':').Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray()).ToLowerInvariant();
        return value.Length == 0 ? null : value;
    }
}

public interface ITextTemplateStore
{
    Task<IReadOnlyList<TextTemplate>> GetAllAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TextTemplate textTemplate, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Text templates, sorted by name.</summary>
public sealed class TextTemplateService(ITextTemplateStore store)
{
    public async Task<IReadOnlyList<TextTemplate>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await store.GetAllAsync(cancellationToken)).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task SaveAsync(TextTemplate textTemplate, CancellationToken cancellationToken = default) => store.SaveAsync(textTemplate, cancellationToken);

    /// <summary>Shortcut → template for writing; an own template wins over the organisation's with the same shortcut.</summary>
    public async Task<IReadOnlyDictionary<string, TextTemplate>> GetShortcutsAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, TextTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in (await store.GetAllAsync(cancellationToken)).OrderBy(t => t.IsFromCloud ? 0 : 1))
        {
            if (TextTemplate.NormalizeShortcut(template.Shortcut) is { } key)
            {
                result[key] = template;
            }
        }

        return result;
    }

    /// <summary>Whether another of the user's own templates already uses this shortcut.</summary>
    public async Task<bool> IsShortcutTakenAsync(string? shortcut, Guid exceptId, CancellationToken cancellationToken = default) =>
        TextTemplate.NormalizeShortcut(shortcut) is { } key
        && (await store.GetAllAsync(cancellationToken)).Any(t => t.Id != exceptId && !t.IsFromCloud && TextTemplate.NormalizeShortcut(t.Shortcut) == key);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => store.DeleteAsync(id, cancellationToken);
}
