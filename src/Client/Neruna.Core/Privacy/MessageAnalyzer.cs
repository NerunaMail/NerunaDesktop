using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Cryptography;
using Neruna.Core.Security;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Privacy;

/// <summary>The overall verdict of <see cref="MessageAnalyzer"/>.</summary>
public enum ThreatLevel
{
    /// <summary>Nothing found that speaks against the mail.</summary>
    Unremarkable,

    /// <summary>Some signs of deception or several small oddities: look closely.</summary>
    Caution,

    /// <summary>Clear signs of a forged sender or deceptive links.</summary>
    Dangerous,
}

/// <summary>A person the user knows: name and addresses (from the contacts), for spotting impostors.</summary>
public sealed record KnownPerson(string Name, IReadOnlyList<string> Addresses);

/// <summary>What the analysis compares with: the user's own addresses and the contacts.</summary>
public sealed record AnalysisContext(IReadOnlyCollection<string> OwnAddresses, IReadOnlyList<KnownPerson> Contacts)
{
    public static AnalysisContext Empty { get; } = new([], []);

    /// <summary>From the accounts (their addresses) and the recipients of the contacts (one entry per address; groups left out).</summary>
    public static AnalysisContext From(IEnumerable<Accounts.Account> accounts, IEnumerable<Contacts.RecipientEntry> recipients)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(recipients);
        var own = accounts.SelectMany(a => a.Identities.Select(i => i.Email).Append(a.EmailAddress)).OfType<string>()
            .Where(e => e.Contains('@', StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var people = recipients.Where(r => !r.IsGroup && r.Name.Trim().Length > 0)
            .GroupBy(r => r.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new KnownPerson(g.Key, g.SelectMany(r => r.Addresses.Select(a => a.Address)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
        return new AnalysisContext(own, people);
    }
}

/// <summary>The sender as the mail states it in its different places.</summary>
/// <param name="Envelope">Return-Path: where bounces go, set by the sending server.</param>
/// <param name="Authentication">The check of the user's own mail server ("SPF: pass, DKIM: pass, DMARC: pass"), if it can be trusted.</param>
/// <param name="Spam">The mail server's spam rating from the headers ("5.8 von 6 Punkten (SpamAssassin)"), if there is one.</param>
public sealed record SenderFacts(string From, string? Envelope, string? ReplyTo, string? Sender, string? Authentication, SpamRating? Spam = null);

/// <summary>
/// What the mail server's spam filter wrote into the headers: a score with its threshold (SpamAssassin, Rspamd), a
/// probability (Sophos) or Microsoft's spam confidence level. The topmost header of each kind – the one of the own
/// server; a sender could add such headers too, so the rating only adds warnings, it never clears a mail.
/// </summary>
public sealed record SpamRating(string Filter, double? Score, double? Threshold, bool IsSpam, string Text)
{
    /// <summary>At least 80 % of the threshold, but not spam: "knapp unter der Schwelle".</summary>
    public bool IsClose => !IsSpam && Score is { } score && Threshold is > 0 and var threshold && score >= threshold * 0.8;
}

/// <summary>The result of <see cref="MessageAnalyzer.Analyze"/>.</summary>
public sealed record ThreatAssessment(ThreatLevel Level, IReadOnlyList<Finding> Findings, SenderFacts Sender, IReadOnlyList<LinkCheck> Links)
{
    /// <summary>The sender failed the authentication of the user's mail server: its address must not be trusted.</summary>
    public bool SenderForged { get; init; }

    public string Summary => Level switch
    {
        ThreatLevel.Dangerous => T("Gefährlich: deutliche Anzeichen für Betrug oder einen gefälschten Absender."),
        ThreatLevel.Caution => T("Vorsicht: einige Merkmale sind auffällig. Prüfen Sie Absender und Links genau."),
        _ => T("Unauffällig: Neruna hat keine typischen Merkmale von Phishing gefunden."),
    };
}

/// <summary>
/// Checks a mail for the usual signs of phishing, all on this device: does the sender's own mail server vouch for the
/// sender (SPF, DKIM, DMARC in Authentication-Results), do From, envelope and Reply-To fit together, does the display name
/// claim another address or a known person, does the domain imitate a known one, do links show another site than they
/// go to, is something hidden, are attachments programs. A help for judging, not a guarantee.
/// </summary>
public static partial class MessageAnalyzer
{
    private static readonly HashSet<string> MacroExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docm", ".dotm", ".xlsm", ".xltm", ".xlsb", ".pptm", ".potm", ".ppsm", ".sldm",
    };

    private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".htm", ".html", ".xhtml", ".shtml", ".svg", ".mht", ".mhtml",
    };

    [GeneratedRegex(@"^\s*(re|aw|antw|sv|vs|r|rif|ref|tr)\s*(\[\d+\])?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+")]
    private static partial Regex AddressInText();

    /// <param name="known">The known phishing hosts; null: <see cref="PhishingList.Shared"/>.</param>
    public static ThreatAssessment Analyze(MimeMessage message, AnalysisContext? context = null, MessageSecurityInfo? security = null, PhishingList? known = null)
    {
        known ??= PhishingList.Shared;
        ArgumentNullException.ThrowIfNull(message);
        context ??= AnalysisContext.Empty;
        var findings = new List<Finding>();

        var from = message.From.Mailboxes.FirstOrDefault();
        var fromAddress = from?.Address ?? string.Empty;
        var fromDomain = DomainNames.OfAddress(fromAddress);
        var envelope = Envelope(message);
        var replyTo = message.ReplyTo.Mailboxes.FirstOrDefault()?.Address;
        var sender = message.Sender?.Address;

        var auth = TrustedAuthentication(message);
        var forged = CheckAuthentication(auth, fromDomain, findings);

        if (from is null)
        {
            findings.Add(new Finding(Severity.Medium, T("Kein Absender"), T("Die Mail nennt keinen Absender.")));
        }
        else
        {
            CheckSender(from, fromDomain, context, forged, findings);
            if (known.Contains(fromDomain))
            {
                findings.Add(new Finding(Severity.High, T("Absender bekannt für Phishing"),
                    F("Die Absenderdomain «{0}» steht auf der Liste bekannter Phishing-Seiten.", DomainNames.ToUnicode(fromDomain!)))
            { Code = FindingCodes.KnownPhishing });
            }
        }

        if (envelope is not null && fromDomain is not null && !DomainNames.SameOwner(DomainNames.OfAddress(envelope), fromDomain))
        {
            findings.Add(new Finding(auth?.DmarcPass == true ? Severity.Info : Severity.Low, T("Umschlag-Absender weicht ab"),
                F("Technisch versendet wurde die Mail von «{0}», angezeigt wird «{1}». Bei Newsletter- und Versanddiensten ist das üblich.", envelope, fromAddress)));
        }

        if (replyTo is not null && fromDomain is not null && !DomainNames.SameOwner(DomainNames.OfAddress(replyTo), fromDomain))
        {
            findings.Add(new Finding(Severity.Medium, T("Antworten gehen an eine andere Adresse"),
                F("Eine Antwort ginge nicht an «{0}», sondern an «{1}».", fromAddress, replyTo)));
        }

        if (sender is not null && !string.Equals(sender, fromAddress, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new Finding(Severity.Info, T("Im Auftrag gesendet"), F("Gesendet hat «{0}» im Namen von «{1}».", sender, fromAddress)));
        }

        if (ReplyPrefix().IsMatch(message.Subject ?? string.Empty) && message.InReplyTo is null && message.References.Count == 0)
        {
            findings.Add(new Finding(Severity.Low, T("Antwort ohne vorherige Nachricht"),
                T("Der Betreff beginnt mit «Re:» oder «AW:», die Mail antwortet aber auf keine Nachricht – ein Trick, um Vertrautheit vorzutäuschen.")));
        }

        var links = CheckBody(message, findings, known);
        var spam = SpamRatings.Of(message);
        if (spam is { IsSpam: true })
        {
            findings.Add(new Finding(Severity.Medium, T("Ihr Mailserver hält die Mail für Spam"), F("Bewertung: {0}.", spam.Text)));
        }
        else if (spam is { IsClose: true })
        {
            findings.Add(new Finding(Severity.Low, T("Knapp unter der Spam-Schwelle"), F("Bewertung Ihres Mailservers: {0}.", spam.Text)));
        }

        CheckAttachments(message, findings);

        if (security is not null)
        {
            foreach (var signature in security.Signatures)
            {
                if (signature.Status == SignatureStatus.Valid && signature.EmailMatchesSender)
                {
                    findings.Add(new Finding(Severity.Good, T("Gültige digitale Signatur"), F("Signiert von {0}.", signature.SignerName)));
                }
                else if (signature.Status == SignatureStatus.Invalid)
                {
                    findings.Add(new Finding(Severity.Medium, T("Ungültige digitale Signatur"), T("Die Mail wurde nach dem Signieren verändert, oder die Signatur ist nicht prüfbar.")));
                }
            }
        }

        var level = findings.Any(f => f.Severity == Severity.High) ? ThreatLevel.Dangerous
            : findings.Any(f => f.Severity == Severity.Medium) || findings.Count(f => f.Severity == Severity.Low) >= 3 ? ThreatLevel.Caution
            : ThreatLevel.Unremarkable;
        var ordered = findings.OrderByDescending(f => f.Severity == Severity.Good ? -1 : (int)f.Severity).ToList();
        var facts = new SenderFacts(from is null ? string.Empty : from.ToString(), envelope, replyTo, sender, auth?.Text, spam);
        return new ThreatAssessment(level, ordered, facts, links) { SenderForged = forged };
    }

    private static string? Envelope(MimeMessage message)
    {
        var value = message.Headers[HeaderId.ReturnPath]?.Trim().Trim('<', '>').Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private sealed record Authentication(string? Spf, string? SpfDomain, string? Dkim, IReadOnlyList<string> DkimDomains, string? Dmarc, string Text)
    {
        public bool DmarcPass => Dmarc == "pass";
    }

    /// <summary>
    /// The result of the user's own mail server: the topmost Authentication-Results header, and only if it can be told
    /// apart from one the sender wrote himself – its server name belongs to a server that received the mail (a Received
    /// header above it), or, without a name (Exchange Online), the mail went through Exchange, which removes such
    /// headers from outside.
    /// </summary>
    private static Authentication? TrustedAuthentication(MimeMessage message)
    {
        var headers = message.Headers;
        var index = -1;
        for (var i = 0; i < headers.Count; i++)
        {
            if (headers[i].Id == HeaderId.AuthenticationResults)
            {
                index = i;
                break;
            }
        }

        if (index < 0 || !AuthenticationResults.TryParse(headers[index].RawValue, out var results))
        {
            return null;
        }

        if (results.AuthenticationServiceIdentifier is { Length: > 0 } server)
        {
            var receivedBy = headers.Take(index + 2).Where(h => h.Id == HeaderId.Received).Select(h => ReceivedBy(h.Value));
            if (!receivedBy.Any(by => by is not null && DomainNames.SameOwner(by, server)))
            {
                return null;
            }
        }
        else if (!headers.Any(h => h.Field.StartsWith("X-MS-Exchange-Organization-", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        string? Result(string method) => results.Results.FirstOrDefault(r => r.Method.Equals(method, StringComparison.OrdinalIgnoreCase))?.Result?.ToLowerInvariant();
        string? Property(string method, string name) => results.Results.Where(r => r.Method.Equals(method, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.Properties).FirstOrDefault(p => p.Property.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

        var dkimDomains = results.Results.Where(r => r.Method.Equals("dkim", StringComparison.OrdinalIgnoreCase) && "pass".Equals(r.Result, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.Properties).Where(p => p.Property.Equals("d", StringComparison.OrdinalIgnoreCase) || p.Property.Equals("i", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Value.Contains('@', StringComparison.Ordinal) ? DomainNames.OfAddress(p.Value) ?? p.Value : p.Value).ToList();
        var dkim = dkimDomains.Count > 0 ? "pass" : Result("dkim");
        var spf = Result("spf");
        var dmarc = Result("dmarc");
        var text = string.Join(", ", new[] { ("SPF", spf), ("DKIM", dkim), ("DMARC", dmarc) }.Where(r => r.Item2 is not null).Select(r => r.Item1 + ": " + r.Item2));
        return new Authentication(spf, DomainNames.OfAddress(Property("spf", "mailfrom")) ?? Property("spf", "mailfrom"), dkim, dkimDomains, dmarc, text);
    }

    // "from x by mx.example.com with ESMTPS id …" → "mx.example.com".
    private static string? ReceivedBy(string value)
    {
        var match = Regex.Match(value, @"\bby\s+([A-Za-z0-9.-]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.TrimEnd('.') : null;
    }

    /// <returns>Whether the sender address must be taken as forged.</returns>
    private static bool CheckAuthentication(Authentication? auth, string? fromDomain, List<Finding> findings)
    {
        if (auth is null)
        {
            findings.Add(new Finding(Severity.Info, T("Keine Absenderprüfung verfügbar"),
                T("Ihr Mailserver hat kein verwertbares Prüfergebnis (SPF, DKIM, DMARC) mitgeliefert. Ob der Absender echt ist, lässt sich nicht bestätigen.")));
            return false;
        }

        switch (auth.Dmarc)
        {
            case "fail":
                findings.Add(new Finding(Severity.High, T("Absenderprüfung fehlgeschlagen (DMARC)"),
                    F("Ihr Mailserver konnte nicht bestätigen, dass die Mail wirklich von «{0}» stammt. Die Absenderadresse ist sehr wahrscheinlich gefälscht.", fromDomain)));
                return true;
            case "pass":
                findings.Add(new Finding(Severity.Good, T("Absender bestätigt (DMARC)"), F("Ihr Mailserver hat bestätigt, dass die Mail von «{0}» stammt.", fromDomain)));
                return false;
        }

        var dkimAligned = auth.DkimDomains.Any(d => DomainNames.SameOwner(d, fromDomain));
        var spfAligned = auth.Spf == "pass" && DomainNames.SameOwner(auth.SpfDomain, fromDomain);
        if (dkimAligned || spfAligned)
        {
            findings.Add(new Finding(Severity.Good, T("Absender bestätigt"), F("Ihr Mailserver hat «{0}» als Absender bestätigt ({1}).", fromDomain, dkimAligned ? "DKIM" : "SPF")));
            return false;
        }

        if (auth.Dkim == "pass" && auth.DkimDomains.Count > 0)
        {
            findings.Add(new Finding(Severity.Low, T("Signiert von einer anderen Domain"),
                F("Bestätigt wurde «{0}», nicht die Absenderdomain «{1}».", auth.DkimDomains[0], fromDomain)));
        }

        if (auth.Spf is "fail" && auth.Dkim != "pass")
        {
            findings.Add(new Finding(Severity.Medium, T("Absenderprüfung fehlgeschlagen (SPF)"), T("Der versendende Server ist für diese Absenderdomain nicht berechtigt.")));
            return true;
        }

        if (auth.Dkim is "fail" && auth.Spf != "pass")
        {
            findings.Add(new Finding(Severity.Medium, T("Signatur des Absenders ungültig (DKIM)"), T("Die Mail wurde unterwegs verändert, oder die Signatur ist gefälscht.")));
            return true;
        }

        if (auth.Spf is "softfail")
        {
            findings.Add(new Finding(Severity.Low, T("Absenderprüfung unsicher (SPF)"), T("Der versendende Server ist für diese Absenderdomain nur bedingt berechtigt.")));
        }

        return false;
    }

    private static void CheckSender(MailboxAddress from, string? fromDomain, AnalysisContext context, bool forged, List<Finding> findings)
    {
        var name = from.Name?.Trim() ?? string.Empty;
        var address = from.Address;

        // "PayPal Service <service@paypal.com>" <x@evil.example>: the name shows an address of its own.
        foreach (Match shown in AddressInText().Matches(name))
        {
            if (!string.Equals(shown.Value, address, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new Finding(Severity.High, T("Anzeigename enthält eine andere Adresse"),
                    F("Angezeigt wird «{0}», tatsächlich stammt die Mail von «{1}».", shown.Value, address)));
                break;
            }
        }

        if (!AddressInText().IsMatch(name) && DomainNameIn(name) is { } named && !DomainNames.SameOwner(named, fromDomain))
        {
            findings.Add(new Finding(Severity.Medium, T("Anzeigename nennt eine andere Domain"),
                F("Der Name nennt «{0}», die Adresse gehört aber zu «{1}».", named, fromDomain)));
        }

        if (fromDomain is not null)
        {
            if (DomainNames.IsMixedScript(DomainNames.ToUnicode(fromDomain)))
            {
                findings.Add(new Finding(Severity.High, T("Gemischte Schriftzeichen in der Absenderdomain"),
                    F("«{0}» mischt Buchstaben verschiedener Schriften, um einen bekannten Namen nachzuahmen.", DomainNames.ToUnicode(fromDomain))));
            }

            // Look-alikes of the own domains and of domains of contacts ("rnicrosoft.com", "neruna-mail.ch" are not caught) –
            // not when the address or its domain is known itself (another address there is in the contacts): two similar
            // domains of real contacts ("bmtreuhand.ch", "bdm-treuhand.ch") are no imitation of each other.
            var known = context.OwnAddresses.Concat(context.Contacts.SelectMany(c => c.Addresses))
                .Select(DomainNames.OfAddress).OfType<string>().Select(d => DomainNames.Registrable(d) ?? d).Distinct(StringComparer.Ordinal).ToList();
            var isContact = context.Contacts.Any(c => c.Addresses.Contains(address, StringComparer.OrdinalIgnoreCase));
            var isKnownDomain = known.Contains(DomainNames.Registrable(fromDomain) ?? fromDomain, StringComparer.OrdinalIgnoreCase);
            if (!isContact && !isKnownDomain && known.FirstOrDefault(k => DomainNames.LooksLike(fromDomain, k)) is { } original)
            {
                var own = context.OwnAddresses.Any(a => DomainNames.SameOwner(DomainNames.OfAddress(a), original));
                findings.Add(new Finding(Severity.High, T("Absenderdomain ahmt eine bekannte nach"),
                    own
                        ? F("«{0}» sieht aus wie Ihre eigene Domain «{1}», ist es aber nicht.", DomainNames.ToUnicode(fromDomain), original)
                        : F("«{0}» sieht aus wie «{1}» (aus Ihren Kontakten), ist es aber nicht.", DomainNames.ToUnicode(fromDomain), original))
            { Code = FindingCodes.Impersonation });
            }
        }

        // "Raiffeisen <raiffeisen4@ferienwohnung-xyz.de>": the name of a bank, post or shop, the address of someone else.
        if (fromDomain is not null && Brands.Claimed(name) is { } brand && !Brands.Owns(brand, fromDomain)
            && !context.Contacts.Any(c => c.Addresses.Contains(address, StringComparer.OrdinalIgnoreCase)))
        {
            findings.Add(new Finding(Severity.High, F("Gibt sich als «{0}» aus", brand.Name),
                F("Der Name nennt «{0}», die Adresse gehört aber zu «{1}» – nicht zu {0}.", brand.Name, DomainNames.ToUnicode(fromDomain)))
            { Code = FindingCodes.Impersonation });
        }

        if (context.OwnAddresses.Any(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase)))
        {
            if (forged)
            {
                findings.Add(new Finding(Severity.High, T("Ihre eigene Adresse als Absender gefälscht"),
                    T("Die Mail gibt sich als von Ihnen gesendet aus, stammt aber nicht von Ihrem Server – typisch für Erpressungsmails («Ihr Konto wurde gehackt»)."))
            { Code = FindingCodes.Impersonation });
            }

            return;
        }

        // A known person's name with a strange address: the classic "CEO fraud".
        var person = context.Contacts.FirstOrDefault(c => c.Addresses.Any(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase)));
        if (person is not null)
        {
            if (!forged)
            {
                findings.Add(new Finding(Severity.Good, T("Bekannter Kontakt"), F("Die Adresse gehört zu «{0}» in Ihren Kontakten.", person.Name)));
            }

            return;
        }

        if (name.Length >= 4 && context.Contacts.FirstOrDefault(c => SameName(c.Name, name)) is { } impostor)
        {
            findings.Add(new Finding(Severity.High, T("Name eines Kontakts, aber fremde Adresse"),
                F("«{0}» ist in Ihren Kontakten mit {1} gespeichert – diese Mail kommt aber von «{2}».", impostor.Name, string.Join(", ", impostor.Addresses), address))
            { Code = FindingCodes.Impersonation });
        }
    }

    private static bool SameName(string a, string b)
    {
        static string Normal(string s) => string.Join(' ', s.ToLowerInvariant().Split([' ', ',', '.', '"', '\''], StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        return a.Trim().Length > 0 && Normal(a) == Normal(b);
    }

    private static string? DomainNameIn(string text)
    {
        var match = Regex.Match(text, @"(?:[\p{L}\p{N}-]+\.)+[\p{L}]{2,24}\b");
        return match.Success && DomainNames.Registrable(match.Value) is not null ? match.Value.ToLowerInvariant() : null;
    }

    private static List<LinkCheck> CheckBody(MimeMessage message, List<Finding> findings, PhishingList known)
    {
        if (message.HtmlBody is not { } html)
        {
            return [];
        }

        var scan = HtmlScan.Of(html);
        var checks = new List<LinkCheck>();
        var seen = new HashSet<(string, string, string?)>();
        foreach (var link in scan.Links)
        {
            if (seen.Add((link.Href, link.Text, link.Title)))
            {
                checks.Add(LinkInspector.Inspect(link.Href, link.Text, link.Title, clean: false, known));
            }
        }

        // One finding per kind, not one per link (a newsletter has dozens).
        foreach (var group in checks.SelectMany(c => c.Findings.Where(f => f.Severity >= Severity.Low).Select(f => (Check: c, Finding: f))).GroupBy(x => x.Finding.Title))
        {
            var worst = group.MaxBy(x => x.Finding.Severity);
            var count = group.Select(x => x.Check).Distinct().Count();
            var detail = count == 1 ? worst.Finding.Detail : F("{0} (und {1} weitere Links)", worst.Finding.Detail, count - 1);
            findings.Add(new Finding(worst.Finding.Severity, worst.Finding.Title, detail) { Code = worst.Finding.Code });
        }

        var hiddenLinks = scan.Links.Count(l => l.Hidden && l.Href.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        if (hiddenLinks > 0)
        {
            findings.Add(new Finding(Severity.Low, T("Versteckte Links"), F("{0} Links sind in der Mail unsichtbar.", hiddenLinks)));
        }

        if (scan.HiddenText.Length > 300)
        {
            findings.Add(new Finding(Severity.Low, T("Versteckter Text"),
                F("Die Mail enthält {0} Zeichen unsichtbaren Text – damit werden oft Spamfilter getäuscht.", scan.HiddenText.Length)));
        }

        return checks.Where(c => c.Findings.Any(f => f.Severity >= Severity.Low)).ToList();
    }

    private static void CheckAttachments(MimeMessage message, List<Finding> findings)
    {
        foreach (var attachment in message.Attachments)
        {
            var name = Mail.MessageContent.FileNameOf(attachment);
            var extension = Path.GetExtension(name.TrimEnd('.', ' '));
            if (OpenedAttachments.IsRisky(name))
            {
                findings.Add(new Finding(Severity.High, T("Programm als Anhang"), F("«{0}» ist ein Programm oder Skript. Öffnen Sie es nur, wenn Sie es erwartet haben.", name)));
            }
            else if (MacroExtensions.Contains(extension))
            {
                findings.Add(new Finding(Severity.Medium, T("Office-Datei mit Makros"), F("«{0}» kann Makros enthalten, die beim Öffnen Programme ausführen.", name)));
            }
            else if (PageExtensions.Contains(extension))
            {
                findings.Add(new Finding(Severity.Medium, T("Webseite als Anhang"), F("«{0}» öffnet sich im Browser – so werden oft gefälschte Anmeldeseiten verschickt.", name)));
            }
        }
    }
}
