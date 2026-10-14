using System.Text.Json;
using Neruna.Contracts.Discovery;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Accounts;

/// <summary>A link to show the user: what it says and where it goes.</summary>
public sealed record LinkItem(string Text, Uri Url);

/// <summary>
/// The provider's webmail, instructions and support from the account's configuration (autoconfig, Neruna's directory,
/// the organisation). Kept with the account's connections (<c>provider.*</c> settings), shown when setting up and in the
/// account settings.
/// </summary>
public static class ProviderLinks
{
    public const string WebmailKey = "provider.webmail";
    public const string DocumentationKey = "provider.documentation";

    private sealed record StoredLink(string Url, Dictionary<string, string> Text);

    /// <summary>The connection settings for these links (nothing without any).</summary>
    public static IReadOnlyDictionary<string, string> ToSettings(MailProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var settings = new Dictionary<string, string>();
        if (config.WebmailUrl is { } webmail)
        {
            settings[WebmailKey] = webmail.AbsoluteUri;
        }

        if (config.Documentation.Count > 0)
        {
            settings[DocumentationKey] = JsonSerializer.Serialize(config.Documentation.Select(d => new StoredLink(d.Url.AbsoluteUri, new Dictionary<string, string>(d.Text))).ToList());
        }

        return settings;
    }

    /// <summary>The links kept with an account (the first connection that has them).</summary>
    public static IReadOnlyList<LinkItem> Of(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var settings = account.Connections.Select(c => c.Settings).FirstOrDefault(s => s.ContainsKey(WebmailKey) || s.ContainsKey(DocumentationKey));
        if (settings is null)
        {
            return [];
        }

        var links = new List<LinkItem>();
        if (Uri.TryCreate(settings.GetValueOrDefault(WebmailKey), UriKind.Absolute, out var webmail))
        {
            links.Add(new LinkItem(T("Webmail"), webmail));
        }

        try
        {
            foreach (var stored in JsonSerializer.Deserialize<List<StoredLink>>(settings.GetValueOrDefault(DocumentationKey) ?? "[]") ?? [])
            {
                if (Uri.TryCreate(stored.Url, UriKind.Absolute, out var url))
                {
                    links.Add(Item(new ProviderLink(url, stored.Text)));
                }
            }
        }
        catch (JsonException)
        {
            // an old or broken value: the webmail at least
        }

        return links;
    }

    /// <summary>Links of a configuration as found during setup: webmail, then the documentation.</summary>
    public static IReadOnlyList<LinkItem> Of(MailProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return [.. (config.WebmailUrl is { } webmail ? [new LinkItem(T("Webmail"), webmail)] : Array.Empty<LinkItem>()),
                .. config.Documentation.Select(Item)];
    }

    /// <summary>A provider link with its text in the UI language; without text: "Anleitung" (or "Support" for an e-mail address).</summary>
    public static LinkItem Item(ProviderLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var text = link.TextFor(Language) ?? (link.Url.Scheme == Uri.UriSchemeMailto ? T("Support") : T("Anleitung"));
        return new LinkItem(text.Length > 80 ? text[..77] + "…" : text, link.Url);
    }
}
