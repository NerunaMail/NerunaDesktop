using System.Text.RegularExpressions;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Privacy;

/// <summary>The result of checking a link before it opens.</summary>
/// <param name="Href">The link as it is in the mail.</param>
/// <param name="Target">Where it really goes (after skipping known redirect services, if wanted); null if it is no web address.</param>
/// <param name="Opens">What opens: <see cref="Target"/> without tracking parameters (if wanted), or the link as it is.</param>
/// <param name="Text">The text the mail shows for it.</param>
/// <param name="Title">The hover text.</param>
public sealed record LinkCheck(string Href, Uri? Target, string Opens, string? Text, string? Title, IReadOnlyList<Finding> Findings)
{
    /// <summary>Something speaks against opening it without a look (medium or high findings).</summary>
    public bool IsSuspicious => Findings.Any(f => f.Severity >= Severity.Medium);

    /// <summary>The server that is really contacted, as shown to a person (Unicode, without "www.").</summary>
    public string? Host => Target?.IdnHost is { } host ? DomainNames.ToUnicode(host) : null;
}

/// <summary>
/// Checks a link from a mail: does its text name another site than it goes to, does the address hide its real server
/// (look-alike characters, "bank.ch@evil.example", bare IP), and cleans it up (tracking parameters, redirect
/// services such as Safe Links). All local: nothing is looked up on the internet.
/// </summary>
public static partial class LinkInspector
{
    // Parameters that only tell the sender who clicked where (campaign and click ids of common mailing and ad systems).
    private static readonly HashSet<string> TrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "gclsrc", "dclid", "gbraid", "wbraid", "msclkid", "yclid", "twclid", "ttclid", "igshid", "li_fat_id",
        "mc_cid", "mc_eid", "_hsenc", "_hsmi", "__hssc", "__hstc", "__hsfp", "hsctatracking", "mkt_tok", "vero_id", "vero_conv",
        "oly_anon_id", "oly_enc_id", "_openstat", "wickedid", "rb_clickid", "s_cid", "sc_cid", "ml_subscriber", "ml_subscriber_hash",
        "ef_id", "_ga", "_gl", "srsltid", "trk", "trkcampaign", "trkinfo", "ss_campaign_id", "ss_email_id", "ss_source", "spm",
        "zanpid", "kclickid", "awt_a", "awt_l", "awt_m", "guccounter", "guce_referrer", "guce_referrer_sig", "cmpid", "ito",
    };

    // Short links: the target cannot be seen before opening.
    private static readonly HashSet<string> Shorteners = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit.ly", "bitly.com", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "buff.ly", "rebrand.ly", "cutt.ly", "s.id",
        "shorturl.at", "tiny.cc", "t.ly", "rb.gy", "v.gd", "lnkd.in", "shorte.st", "adf.ly", "bl.ink", "short.io", "tiny.one",
    };

    // Mailing services that count clicks by sending every link through their own server first (own tracking domains such as
    // "click.shop.ch" need no list: they belong to the same owner as "shop.ch").
    [GeneratedRegex(@"(^|\.)(list-manage\.com|mailchimp\.com|sendgrid\.net|mandrillapp\.com|mailjet\.com|createsend\d*\.com|cmail\d+\.com|sparkpostmail\.com|mailgun\.org|hubspotlinks\.com|hs-sites\.com|awstrack\.me|rs6\.net|exacttarget\.com|sendinblue\.com|brevo\.com|sendibt\d\.com|mjt\.lu|klaviyo\.com|substack\.com|cleverreach\.com|newsletter2go\.com|rapidmail\.de|mailpoet\.com|campaign-archive\.com|salesforce\.com|mktotracking\.com|mkt\d+\.com|eloqua\.com)$", RegexOptions.IgnoreCase)]
    private static partial Regex ClickTracker();

    // A domain name in a link text or hover text: "www.bank.ch", "https://bank.ch/login", "bank.ch".
    [GeneratedRegex(@"(?:https?://)?(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?\.)+[\p{L}]{2,24}(?![\p{L}\p{N}@-])", RegexOptions.IgnoreCase)]
    private static partial Regex DomainInText();

    /// <summary>Checks <paramref name="href"/>; <paramref name="clean"/>: open it without tracking parameters and redirect services.</summary>
    public static LinkCheck Inspect(string href, string? text = null, string? title = null, bool clean = true)
    {
        ArgumentNullException.ThrowIfNull(href);
        var findings = new List<Finding>();
        href = href.Trim();
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            if (uri?.Scheme is "javascript" or "data" or "vbscript" or "file")
            {
                findings.Add(new Finding(Severity.High, T("Kein gewöhnlicher Weblink"), F("Der Link beginnt mit «{0}:» und könnte etwas auf Ihrem Gerät ausführen oder öffnen.", uri.Scheme)));
            }

            return new LinkCheck(href, null, href, text, title, findings);
        }

        var target = Unwrap(uri, out var via);
        if (via is not null)
        {
            findings.Add(new Finding(clean ? Severity.Info : Severity.Low, F("Weiterleitung über {0}", via),
                clean ? T("Neruna öffnet direkt das eigentliche Ziel.") : T("Das eigentliche Ziel steht im Link; geöffnet wird zuerst der Weiterleitungsdienst.")));
        }

        var host = target.IdnHost;
        if (!string.IsNullOrEmpty(target.UserInfo))
        {
            findings.Add(new Finding(Severity.High, T("Täuschende Adresse"),
                F("Vor dem «@» steht «{0}» – geöffnet wird aber «{1}».", Uri.UnescapeDataString(target.UserInfo), DomainNames.ToUnicode(host))));
        }

        if (DomainNames.IsIpAddress(host))
        {
            findings.Add(new Finding(Severity.Medium, T("IP-Adresse statt Name"), T("Seriöse Absender verlinken auf einen Namen, nicht auf eine nackte IP-Adresse.")));
        }
        else if (DomainNames.IsMixedScript(DomainNames.ToUnicode(host)))
        {
            findings.Add(new Finding(Severity.High, T("Gemischte Schriftzeichen im Namen"),
                F("«{0}» mischt Buchstaben verschiedener Schriften (z. B. kyrillisch und lateinisch) – ein bekannter Trick, um bekannte Namen nachzuahmen. Tatsächlich: {1}", DomainNames.ToUnicode(host), host)));
        }
        else if (DomainNames.IsInternational(host))
        {
            findings.Add(new Finding(Severity.Low, T("Name mit Sonderzeichen"), F("«{0}» wird technisch als «{1}» aufgerufen.", DomainNames.ToUnicode(host), host)));
        }

        if (Shorteners.Contains(host))
        {
            findings.Add(new Finding(Severity.Low, T("Kurzlink"), T("Wohin der Link führt, zeigt sich erst beim Öffnen.")));
        }

        if (target.Scheme == "http")
        {
            findings.Add(new Finding(Severity.Low, T("Unverschlüsselt"), T("Die Seite wird ohne HTTPS geöffnet; Eingaben dort sind nicht geschützt.")));
        }

        // The text or hover text names a site – is it the one the link goes to?
        foreach (var (shown, isTitle) in new[] { (text, false), (title, true) })
        {
            if (DomainShown(shown) is not { } named || DomainNames.SameOwner(named, host))
            {
                continue;
            }

            var tracker = via is null && ClickTracker().IsMatch(uri.IdnHost);
            var lookalike = DomainNames.LooksLike(host, named);
            findings.Add(new Finding(
                tracker && !lookalike ? Severity.Low : Severity.High,
                isTitle ? T("Hover-Text zeigt eine andere Adresse") : T("Linktext zeigt eine andere Adresse"),
                tracker && !lookalike
                    ? F("Angezeigt wird «{0}», der Link geht aber zuerst zu «{1}» (oft ein Klickzähler von Newsletter-Diensten).", named, DomainNames.ToUnicode(host))
                    : F("Angezeigt wird «{0}», der Link führt aber zu «{1}».", named, DomainNames.ToUnicode(host))));
        }

        var opens = target;
        if (clean)
        {
            opens = WithoutTracking(target, out var removed);
            if (removed > 0)
            {
                findings.Add(new Finding(Severity.Info, T("Tracking-Parameter"), F("{0} Parameter, die nur der Auswertung dienen, werden beim Öffnen entfernt.", removed)));
            }
        }

        return new LinkCheck(href, target, clean ? opens.AbsoluteUri : href, text, title, findings);
    }

    /// <summary>The link as it opens with "Links bereinigen": redirect services skipped, tracking parameters removed.</summary>
    public static string Clean(string href) => Inspect(href, clean: true).Opens;

    // The site a text names, if it is (mostly) an address: "www.bank.ch", "Login: https://bank.ch/x". Not every word with a dot.
    private static string? DomainShown(string? shown)
    {
        if (string.IsNullOrWhiteSpace(shown) || DomainInText().Match(shown) is not { Success: true } match)
        {
            return null;
        }

        var value = match.Value;
        var host = value.Contains("://", StringComparison.Ordinal) ? value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..] : value;
        // Only a known public suffix makes it a domain ("Hallo.Welt" or "Version 2.0" are not).
        return DomainNames.Registrable(host) is not null ? host.ToLowerInvariant() : null;
    }

    /// <summary>Skips redirect services that carry the real target in the link (Safe Links, Google, Facebook …).</summary>
    private static Uri Unwrap(Uri uri, out string? via)
    {
        via = null;
        for (var depth = 0; depth < 3; depth++)
        {
            var host = uri.IdnHost.ToLowerInvariant();
            string? inner = null;
            string? service = null;
            if (host.EndsWith("safelinks.protection.outlook.com", StringComparison.Ordinal))
            {
                (inner, service) = (Query(uri, "url"), "Safe Links");
            }
            else if (host.StartsWith("www.google.", StringComparison.Ordinal) || host.StartsWith("google.", StringComparison.Ordinal))
            {
                (inner, service) = (uri.AbsolutePath == "/url" ? Query(uri, "q") ?? Query(uri, "url") : null, "Google");
            }
            else if (host is "l.facebook.com" or "lm.facebook.com" or "l.instagram.com")
            {
                (inner, service) = (Query(uri, "u"), "Facebook");
            }
            else if (host.EndsWith("linkedin.com", StringComparison.Ordinal) && uri.AbsolutePath.StartsWith("/redir/", StringComparison.Ordinal))
            {
                (inner, service) = (Query(uri, "url"), "LinkedIn");
            }
            else if (host.EndsWith("youtube.com", StringComparison.Ordinal) && uri.AbsolutePath == "/redirect")
            {
                (inner, service) = (Query(uri, "q"), "YouTube");
            }
            else if (host == "smex-ctp.trendmicro.com")
            {
                (inner, service) = (Query(uri, "url"), "Trend Micro");
            }
            else if (host.EndsWith(".awstrack.me", StringComparison.Ordinal))
            {
                // https://xyz.r.eu-west-1.awstrack.me/L0/https:%2F%2Fexample.com%2F/1/…
                var segments = uri.AbsolutePath.Split('/');
                (inner, service) = (segments.Length > 2 ? Uri.UnescapeDataString(segments[2]) : null, "Amazon SES");
            }

            if (inner is null || !Uri.TryCreate(inner, UriKind.Absolute, out var next) || next.Scheme is not ("http" or "https"))
            {
                return uri;
            }

            via ??= service;
            uri = next;
        }

        return uri;
    }

    private static string? Query(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }

        return null;
    }

    // Removes known tracking parameters; the others stay exactly as they were (encoding and order).
    private static Uri WithoutTracking(Uri uri, out int removed)
    {
        removed = 0;
        if (uri.Query.Length <= 1)
        {
            return uri;
        }

        var kept = new List<string>();
        foreach (var pair in uri.Query[1..].Split('&'))
        {
            var name = Uri.UnescapeDataString(pair.Split('=')[0]);
            if (name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("elq", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("pk_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("mtm_", StringComparison.OrdinalIgnoreCase)
                || TrackingParameters.Contains(name))
            {
                removed++;
            }
            else if (pair.Length > 0)
            {
                kept.Add(pair);
            }
        }

        if (removed == 0)
        {
            return uri;
        }

        var builder = new UriBuilder(uri) { Query = string.Join('&', kept) };
        if (uri.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }
}
