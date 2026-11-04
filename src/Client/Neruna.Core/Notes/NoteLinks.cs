using System.Net;
using System.Text.RegularExpressions;
using System.Web;

namespace Neruna.Core.Notes;

public enum NoteLinkKind
{
    Mail,
    Contact,
    Event,
}

/// <summary>A link in a note to a mail (by Message-ID), a contact (by UID and address) or an appointment (by UID).</summary>
/// <param name="Value">Message-ID (without &lt;&gt;), contact UID or iCalendar UID.</param>
/// <param name="Address">A contact's email address (found by it when the UID is unknown here).</param>
public sealed record NoteLink(NoteLinkKind Kind, string Value, string? Address = null);

/// <summary>
/// The links live in the note's HTML, so they travel with it to every device and server: a mail as "mid:" URL
/// (RFC 2392, the Message-ID stays when the mail moves), contacts and appointments as "neruna://" URLs. From them and
/// from addresses in the text the store keeps keys to find a contact's or a mail's notes.
/// </summary>
public static partial class NoteLinks
{
    public static string MailHref(string messageId) => "mid:" + Uri.EscapeDataString(messageId.Trim().Trim('<', '>'));

    public static string ContactHref(string? uid, string? address) =>
        "neruna://contact?" + string.Join("&", new[] { ("uid", uid), ("email", address) }
            .Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2!)));

    public static string EventHref(string uid) => "neruna://event?uid=" + Uri.EscapeDataString(uid);

    /// <summary>An anchor for the note: <paramref name="text"/> linked to <paramref name="href"/>.</summary>
    public static string Anchor(string href, string text) =>
        $"<a href=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(text)}</a>";

    /// <summary>The link behind a URL of a note, or null for an ordinary web link.</summary>
    public static NoteLink? Parse(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        href = WebUtility.HtmlDecode(href.Trim());
        if (href.StartsWith("mid:", StringComparison.OrdinalIgnoreCase))
        {
            var id = Uri.UnescapeDataString(href[4..]).Trim('<', '>');
            return id.Length == 0 ? null : new NoteLink(NoteLinkKind.Mail, id);
        }

        if (!href.StartsWith("neruna://", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        var uid = query["uid"];
        return uri.Host.ToLowerInvariant() switch
        {
            "contact" when !string.IsNullOrEmpty(uid) || !string.IsNullOrEmpty(query["email"]) =>
                new NoteLink(NoteLinkKind.Contact, uid ?? string.Empty, query["email"]),
            "event" when !string.IsNullOrEmpty(uid) => new NoteLink(NoteLinkKind.Event, uid),
            _ => null,
        };
    }

    /// <summary>Every link in the HTML.</summary>
    public static IReadOnlyList<NoteLink> Find(string html) =>
        [.. HrefPattern().Matches(html ?? string.Empty).Select(m => Parse(m.Groups[1].Value)).OfType<NoteLink>().Distinct()];

    /// <summary>
    /// The keys the store indexes: "mid:…", "contact:…", "event:…" and "email:…" (from contact links and from
    /// addresses written in the text).
    /// </summary>
    public static IReadOnlyList<string> Keys(string html)
    {
        var keys = new List<string>();
        foreach (var link in Find(html))
        {
            switch (link.Kind)
            {
                case NoteLinkKind.Mail:
                    keys.Add(MailKey(link.Value));
                    break;
                case NoteLinkKind.Contact:
                    if (link.Value.Length > 0)
                    {
                        keys.Add(ContactKey(link.Value));
                    }

                    if (link.Address is { Length: > 0 } address)
                    {
                        keys.Add(AddressKey(address));
                    }

                    break;
                case NoteLinkKind.Event:
                    keys.Add(EventKey(link.Value));
                    break;
            }
        }

        keys.AddRange(NoteText.Addresses(NoteText.PlainText(html ?? string.Empty)).Select(AddressKey));
        return [.. keys.Distinct(StringComparer.Ordinal)];
    }

    public static string MailKey(string messageId) => "mid:" + messageId.Trim().Trim('<', '>');

    public static string ContactKey(string uid) => "contact:" + uid;

    public static string EventKey(string uid) => "event:" + uid;

    public static string AddressKey(string address) => "email:" + address.Trim().ToLowerInvariant();

    [GeneratedRegex("""href\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefPattern();
}
