using System.Text.RegularExpressions;

namespace Neruna.Core.Mail;

/// <summary>
/// A mail signature as HTML (images inline as data: URIs, like in the editor). Later Neruna Cloud can deliver central
/// signatures and templates; <see cref="Source"/> keeps them apart from the user's own.
/// </summary>
public sealed record Signature(Guid Id, string Name, string Html, DateTimeOffset UpdatedAt, string Source = Signature.LocalSource)
{
    public const string LocalSource = "local";

    /// <summary>Kept by the organisation in Neruna Cloud: read-only here, replaced on every refresh ("cloud:&lt;id&gt;" from further organisations).</summary>
    public const string CloudSource = Cloud.CloudSources.Cloud;

    public bool IsFromCloud => Cloud.CloudSources.IsCloud(Source);
}

public interface ISignatureStore
{
    Task<IReadOnlyList<Signature>> GetAllAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(Signature signature, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Which signature an account uses by default (one for new messages, one for replies/forwards).</summary>
public sealed record SignatureAssignment(Guid? NewMessages, Guid? RepliesAndForwards);

public enum ComposeKind
{
    New,
    Reply,
    Forward,
}

/// <summary>Signatures and their per-account defaults (assignments live in the settings store).</summary>
public sealed class SignatureService(ISignatureStore store, ISettingsStore settings)
{
    public async Task<IReadOnlyList<Signature>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await store.GetAllAsync(cancellationToken)).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task SaveAsync(Signature signature, CancellationToken cancellationToken = default) => store.SaveAsync(signature, cancellationToken);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => store.DeleteAsync(id, cancellationToken);

    public async Task<SignatureAssignment> GetAssignmentAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        new(await GetIdAsync(NewKey(accountId), cancellationToken), await GetIdAsync(ReplyKey(accountId), cancellationToken));

    public async Task SetAssignmentAsync(Guid accountId, SignatureAssignment assignment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        await settings.SetAsync(NewKey(accountId), assignment.NewMessages?.ToString(), cancellationToken);
        await settings.SetAsync(ReplyKey(accountId), assignment.RepliesAndForwards?.ToString(), cancellationToken);
    }

    /// <summary>The signature to insert when composing; null if none is assigned (or it was deleted meanwhile).</summary>
    public async Task<Signature?> ResolveAsync(Guid accountId, ComposeKind kind, CancellationToken cancellationToken = default)
    {
        var assignment = await GetAssignmentAsync(accountId, cancellationToken);
        var id = kind == ComposeKind.New ? assignment.NewMessages : assignment.RepliesAndForwards;
        return id is null ? null : (await store.GetAllAsync(cancellationToken)).FirstOrDefault(s => s.Id == id);
    }

    /// <summary>
    /// The signature for a mail to <paramref name="recipients"/>: the one last sent to the first of them Neruna
    /// remembers one for (e.g. «Du» instead of the account's «Sie»), else the account's default.
    /// </summary>
    public async Task<Signature?> ResolveAsync(Guid accountId, ComposeKind kind, IEnumerable<string> recipients, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        foreach (var recipient in recipients.Where(r => r.Contains('@', StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            switch (await settings.GetAsync(RecipientKey(accountId, recipient), cancellationToken))
            {
                case NoSignature:
                    return null;
                case { } stored when Guid.TryParse(stored, out var id) && (await store.GetAllAsync(cancellationToken)).FirstOrDefault(s => s.Id == id) is { } remembered:
                    return remembered;
            }
        }

        return await ResolveAsync(accountId, kind, cancellationToken);
    }

    /// <summary>
    /// After sending: the signature used is remembered for each recipient – only where it differs from the account's
    /// default (the same as the default again: forgotten, so a changed default applies to them as well).
    /// </summary>
    /// <param name="used">The signature in the mail; null: none.</param>
    public async Task RememberAsync(Guid accountId, ComposeKind kind, IEnumerable<string> recipients, Signature? used, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        var standard = await ResolveAsync(accountId, kind, cancellationToken);
        var value = used?.Id == standard?.Id ? null : used?.Id.ToString() ?? NoSignature;
        foreach (var recipient in recipients.Where(r => r.Contains('@', StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await settings.SetAsync(RecipientKey(accountId, recipient), value, cancellationToken);
        }
    }

    private const string NoSignature = "none";

    /// <summary>Settings key prefix of the signatures remembered per recipient.</summary>
    public const string RecipientPrefix = "signature.to.";

    private static string RecipientKey(Guid accountId, string recipient) => $"{RecipientPrefix}{accountId:N}.{recipient.Trim().ToLowerInvariant()}";

    private async Task<Guid?> GetIdAsync(string key, CancellationToken cancellationToken) =>
        Guid.TryParse(await settings.GetAsync(key, cancellationToken), out var id) ? id : null;

    private static string NewKey(Guid accountId) => $"signature.new.{accountId:N}";

    private static string ReplyKey(Guid accountId) => $"signature.reply.{accountId:N}";
}

/// <summary>Places the signature in a draft: below the space for the text, above a quoted original.</summary>
public static partial class SignatureBlock
{
    /// <summary>The editor finds (and replaces) the signature by this id; the first match is always ours, above any quote.</summary>
    public const string ElementId = "neruna-signature";

    public static ComposeDraft Apply(ComposeDraft draft, Signature? signature)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var html = signature?.Html ?? string.Empty;

        // The container is inserted even without a signature, so one can be chosen later in the same place.
        var existing = draft.HtmlBody ?? (draft.Body.Length > 0 ? MessageContent.PlainTextToHtml(draft.Body) : string.Empty);
        var rest = LeadingEmptyLines().Replace(existing, string.Empty);
        var lead = string.IsNullOrEmpty(draft.LeadHtml) ? "<div><br></div>" : draft.LeadHtml;
        var htmlBody = lead + "<div><br></div>" + Element(html) + (rest.Length > 0 ? "<div><br></div>" + rest : string.Empty);

        var plainSignature = signature is null ? string.Empty : HtmlText.ToPlainText(html);
        var plainLead = string.IsNullOrEmpty(draft.LeadHtml) ? string.Empty : HtmlText.ToPlainText(draft.LeadHtml);
        var body = plainSignature.Length == 0 && plainLead.Length == 0 ? draft.Body
            : plainLead + "\n\n" + plainSignature + (draft.Body.Length > 0 ? "\n" + draft.Body.TrimStart('\r', '\n') : string.Empty);
        return draft with { HtmlBody = htmlBody, Body = body, LeadHtml = null };
    }

    public static string Element(string html) => $"<div id=\"{ElementId}\">{html}</div>";

    [GeneratedRegex(@"^(\s*<div><br\s*/?></div>)+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingEmptyLines();
}
