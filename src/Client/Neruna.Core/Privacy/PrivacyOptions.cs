using MimeKit;

namespace Neruna.Core.Privacy;

/// <summary>Pictures and styles from the internet in a mail.</summary>
public enum RemoteContentPolicy
{
    /// <summary>Never loaded, not even on request.</summary>
    Block,

    /// <summary>Loaded on request, or at once for trusted senders (default).</summary>
    Ask,

    /// <summary>Always loaded (tracking pixels still blocked if <see cref="PrivacyOptions.BlockTrackers"/>).</summary>
    Allow,
}

/// <summary>When a link in a mail is shown to the user before it opens.</summary>
public enum LinkConfirmation
{
    Never,

    /// <summary>Only when the link check finds something (default).</summary>
    Suspicious,

    Always,
}

/// <summary>Einstellungen → Datenschutz: what is blocked, what is asked for. Shared by desktop and app.</summary>
public sealed record PrivacyOptions
{
    public RemoteContentPolicy RemoteContent { get; init; } = RemoteContentPolicy.Ask;

    /// <summary>Addresses ("anna@example.ch") and domains ("@example.ch") whose pictures load without asking.</summary>
    public IReadOnlyList<string> TrustedSenders { get; init; } = [];

    /// <summary>Addresses and domains whose links to files (file:…) are links that open the file; for all others text.</summary>
    public IReadOnlyList<string> FileLinkSenders { get; init; } = [];

    public bool BlockTrackers { get; init; } = true;

    public bool PlainText { get; init; }

    public LinkConfirmation ConfirmLinks { get; init; } = LinkConfirmation.Suspicious;

    public bool CleanLinks { get; init; } = true;

    public bool WarnSuspicious { get; init; } = true;

    public bool HideTimeZone { get; init; }

    /// <summary>The check's note "no spam filter – spam is checked by the mail server" is folded behind an "i".</summary>
    public bool HideSpamNote { get; init; }

    /// <summary>Known phishing hosts (list from neruna.cloud, checked on the device; only the download leaves it).</summary>
    public bool PhishingList { get; init; } = true;

    public static async Task<PrivacyOptions> LoadAsync(ISettingsStore settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PrivacyOptions
        {
            RemoteContent = await settings.GetAsync(SettingKeys.PrivacyRemoteContent, cancellationToken) switch
            {
                "block" => RemoteContentPolicy.Block,
                "allow" => RemoteContentPolicy.Allow,
                _ => RemoteContentPolicy.Ask,
            },
            TrustedSenders = await settings.GetListAsync(SettingKeys.PrivacyTrustedSenders, cancellationToken) ?? [],
            FileLinkSenders = await settings.GetListAsync(SettingKeys.PrivacyFileLinkSenders, cancellationToken) ?? [],
            BlockTrackers = await settings.GetBoolAsync(SettingKeys.PrivacyBlockTrackers, fallback: true, cancellationToken),
            PlainText = await settings.GetBoolAsync(SettingKeys.PrivacyPlainText, cancellationToken: cancellationToken),
            ConfirmLinks = await settings.GetAsync(SettingKeys.PrivacyConfirmLinks, cancellationToken) switch
            {
                "never" => LinkConfirmation.Never,
                "always" => LinkConfirmation.Always,
                _ => LinkConfirmation.Suspicious,
            },
            CleanLinks = await settings.GetBoolAsync(SettingKeys.PrivacyCleanLinks, fallback: true, cancellationToken),
            WarnSuspicious = await settings.GetBoolAsync(SettingKeys.PrivacyWarnSuspicious, fallback: true, cancellationToken),
            HideTimeZone = await settings.GetBoolAsync(SettingKeys.PrivacyHideTimeZone, cancellationToken: cancellationToken),
            HideSpamNote = await settings.GetBoolAsync(SettingKeys.PrivacyHideSpamNote, cancellationToken: cancellationToken),
            PhishingList = await settings.GetBoolAsync(SettingKeys.PrivacyPhishingList, fallback: true, cancellationToken),
        };
    }

    public async Task SaveAsync(ISettingsStore settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await settings.SetAsync(SettingKeys.PrivacyRemoteContent, RemoteContent.ToString().ToLowerInvariant(), cancellationToken);
        await settings.SetListAsync(SettingKeys.PrivacyTrustedSenders, TrustedSenders, cancellationToken);
        await settings.SetListAsync(SettingKeys.PrivacyFileLinkSenders, FileLinkSenders, cancellationToken);
        await settings.SetBoolAsync(SettingKeys.PrivacyBlockTrackers, BlockTrackers, cancellationToken);
        await settings.SetBoolAsync(SettingKeys.PrivacyPlainText, PlainText, cancellationToken);
        await settings.SetAsync(SettingKeys.PrivacyConfirmLinks, ConfirmLinks.ToString().ToLowerInvariant(), cancellationToken);
        await settings.SetBoolAsync(SettingKeys.PrivacyCleanLinks, CleanLinks, cancellationToken);
        await settings.SetBoolAsync(SettingKeys.PrivacyWarnSuspicious, WarnSuspicious, cancellationToken);
        await settings.SetBoolAsync(SettingKeys.PrivacyHideTimeZone, HideTimeZone, cancellationToken);
        await settings.SetBoolAsync(SettingKeys.PrivacyHideSpamNote, HideSpamNote, cancellationToken);
        await settings.SetBoolAsync(SettingKeys.PrivacyPhishingList, PhishingList, cancellationToken);
    }

    /// <summary>
    /// An entry for the trusted senders as typed: "Anna@Firma.ch" → "anna@firma.ch", "firma.ch" or "@firma.ch" →
    /// "@firma.ch"; null if it is neither an address nor a domain with a known suffix.
    /// </summary>
    public static string? TrustedEntry(string? input)
    {
        var text = input?.Trim().Trim('<', '>').Trim();
        if (string.IsNullOrEmpty(text) || text.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var at = text.LastIndexOf('@');
        var domain = DomainNames.ToAscii(at >= 0 ? text[(at + 1)..] : text);
        if (domain is null || DomainNames.Registrable(domain) is null || (at > 0 && text[..at].Contains('@', StringComparison.Ordinal)))
        {
            return null;
        }

        return at > 0 ? text[..at].ToLowerInvariant() + "@" + domain : "@" + domain;
    }

    /// <summary>The sender is on the trusted list, by address or by domain.</summary>
    public bool IsTrusted(MailboxAddress? sender) => Matches(TrustedSenders, sender);

    /// <summary>
    /// Links to files in this sender's mails are links that open the file: only for the senders set for it, and never
    /// when the sender fails the authentication check (its address may be forged).
    /// </summary>
    public bool OpensFileLinks(MailboxAddress? sender, bool senderForged = false) => !senderForged && Matches(FileLinkSenders, sender);

    private static bool Matches(IReadOnlyList<string> entries, MailboxAddress? sender)
    {
        if (sender?.Address is not { Length: > 0 } address)
        {
            return false;
        }

        var domain = "@" + DomainNames.OfAddress(address);
        return entries.Any(t => string.Equals(t, address, StringComparison.OrdinalIgnoreCase) || string.Equals(t, domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the pictures of a mail load right away: never when blocked, always when allowed; when asking, for
    /// trusted senders and for mails whose pictures the user loaded before (<paramref name="loadedBefore"/>).
    /// A sender that fails the authentication check is never trusted (its address may be forged).
    /// </summary>
    public bool LoadsRemoteContent(MailboxAddress? sender, bool loadedBefore, bool senderForged = false) => RemoteContent switch
    {
        RemoteContentPolicy.Block => false,
        RemoteContentPolicy.Allow => true,
        _ => loadedBefore || (IsTrusted(sender) && !senderForged),
    };

    /// <summary>Whether a link is shown before it opens, given what the link check found.</summary>
    public bool Confirms(LinkCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return ConfirmLinks switch
        {
            LinkConfirmation.Always => true,
            LinkConfirmation.Never => false,
            _ => check.IsSuspicious,
        };
    }
}
