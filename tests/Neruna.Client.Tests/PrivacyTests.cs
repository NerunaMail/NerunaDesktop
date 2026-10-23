using MimeKit;
using Neruna.Core;
using Neruna.Core.Mail;
using Neruna.Core.Privacy;

namespace Neruna.Client.Tests;

public class PrivacyTests
{
    [Theory]
    [InlineData("mail.google.com", "google.com")]
    [InlineData("shop.example.co.uk", "example.co.uk")]
    [InlineData("anna.github.io", "anna.github.io")]
    [InlineData("www.bücher.ch", "xn--bcher-kva.ch")]
    [InlineData("example.ch", "example.ch")]
    public void Registrable_domain_follows_the_public_suffix_list(string host, string expected) =>
        Assert.Equal(expected, DomainNames.Registrable(host));

    [Fact]
    public void Suffixes_and_ip_addresses_have_no_registrable_domain()
    {
        Assert.Null(DomainNames.Registrable("co.uk"));
        Assert.Null(DomainNames.Registrable("192.168.1.1"));
    }

    [Fact]
    public void Mixed_scripts_are_noticed_but_one_script_is_fine()
    {
        Assert.True(DomainNames.IsMixedScript("pаypal.com")); // Cyrillic "а"
        Assert.False(DomainNames.IsMixedScript("paypal.com"));
        Assert.False(DomainNames.IsMixedScript("пример.рф"));
        Assert.False(DomainNames.IsMixedScript("bücher.ch"));
    }

    [Theory]
    [InlineData("paypa1.com", "paypal.com", true)]
    [InlineData("rnicrosoft.com", "microsoft.com", true)]
    [InlineData("neruma.ch", "neruna.ch", true)]
    [InlineData("login.neruna.ch", "neruna.ch", false)]
    [InlineData("neruna.de", "neruna.ch", false)]
    [InlineData("example.ch", "neruna.ch", false)]
    [InlineData("gmail.com", "ymail.com", false)] // two real providers, no copy of each other
    [InlineData("ymail.com", "gmail.com", false)]
    [InlineData("gmx.net", "gmx.ch", false)]
    [InlineData("gmai1.com", "gmail.com", true)] // but a fake of a provider still is one
    [InlineData("bluewln.ch", "bluewin.ch", true)]
    public void Look_alike_domains_are_recognised(string candidate, string original, bool expected) =>
        Assert.Equal(expected, DomainNames.LooksLike(candidate, original));

    [Fact]
    public void Tracking_parameters_are_removed_and_the_rest_is_kept()
    {
        var cleaned = LinkInspector.Clean("https://shop.example.ch/artikel?id=42&utm_source=newsletter&fbclid=abc&farbe=rot");

        Assert.Equal("https://shop.example.ch/artikel?id=42&farbe=rot", cleaned);
    }

    [Fact]
    public void Safe_links_are_unwrapped()
    {
        var check = LinkInspector.Inspect("https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fexample.ch%2Fseite%3Futm_medium%3Dmail&data=x&reserved=0");

        Assert.Equal("https://example.ch/seite", check.Opens);
        Assert.Equal("example.ch", check.Host);
        Assert.False(check.IsSuspicious);
    }

    [Fact]
    public void A_link_text_naming_another_site_is_dangerous()
    {
        var check = LinkInspector.Inspect("https://login-secure.example.net/paypal", "www.paypal.com");

        Assert.True(check.IsSuspicious);
        Assert.Contains(check.Findings, f => f.Severity == Severity.High);
    }

    [Fact]
    public void A_hover_text_naming_another_site_is_noticed()
    {
        var check = LinkInspector.Inspect("https://evil.example/", "Hier klicken", "https://www.ubs.com/login");

        Assert.True(check.IsSuspicious);
    }

    [Fact]
    public void Own_tracking_subdomains_and_plain_texts_are_fine()
    {
        Assert.False(LinkInspector.Inspect("https://click.shop.ch/x/123", "www.shop.ch").IsSuspicious);
        Assert.False(LinkInspector.Inspect("https://www.shop.ch/angebot", "Zum Angebot").IsSuspicious);
        Assert.False(LinkInspector.Inspect("https://www.shop.ch/v2", "Version 2.0 ist da").IsSuspicious);
    }

    [Fact]
    public void A_file_name_in_a_link_text_is_no_address()
    {
        // A link preview: the site's icon as a linked picture with the alt text "favicon.ico" – ".ico" is no top-level domain.
        var html = """<a href="https://security.example.ch/advisory"><img src="cid:icon" alt="favicon.ico"></a>""";
        var link = Assert.Single(HtmlScan.Of(html).Links);
        Assert.False(LinkInspector.Inspect(link.Href, link.Text, link.Title).IsSuspicious);
        Assert.False(LinkInspector.Inspect("https://www.shop.ch/x", "logo.png").IsSuspicious);

        // A real top-level domain that looks like a file ending still counts: ".zip" exists.
        Assert.True(LinkInspector.Inspect("https://evil.example/", "rechnung.zip").IsSuspicious);
    }

    [Fact]
    public void An_address_written_without_http_is_no_unencrypted_link_and_opens_with_https()
    {
        static bool Unencrypted(LinkCheck c) => c.Findings.Any(f => f.Title == Neruna.Core.Localization.Texts.T("Unverschlüsselt"));

        // The sender's mail program made http://www.firma.ch out of "www.firma.ch".
        var auto = LinkInspector.Inspect("http://www.firma.ch/", "www.firma.ch");
        Assert.False(Unencrypted(auto));
        Assert.Equal("https://www.firma.ch/", auto.Opens);
        Assert.Equal("https://firma.ch/kontakt", LinkInspector.Inspect("http://firma.ch/kontakt", "firma.ch/kontakt").Opens);

        // Written with http://, another text, another site, or not cleaned: as before.
        Assert.True(Unencrypted(LinkInspector.Inspect("http://www.firma.ch/", "http://www.firma.ch")));
        Assert.True(Unencrypted(LinkInspector.Inspect("http://www.firma.ch/", "Unsere Website")));
        Assert.True(Unencrypted(LinkInspector.Inspect("http://www.firma.ch/", "www.andere.ch")));
        Assert.Equal("http://www.firma.ch/", LinkInspector.Inspect("http://www.firma.ch/", "www.firma.ch", clean: false).Opens);
    }

    [Fact]
    public void Newsletter_click_counters_are_not_suspicious()
    {
        var check = LinkInspector.Inspect("https://u123.ct.sendgrid.net/ls/click?upn=abc", "www.shop.ch");

        Assert.False(check.IsSuspicious);
        Assert.NotEmpty(check.Findings);
    }

    [Fact]
    public void User_info_before_the_host_is_dangerous()
    {
        var check = LinkInspector.Inspect("https://www.postfinance.ch@evil.example/login");

        Assert.True(check.IsSuspicious);
        Assert.Equal("evil.example", check.Host);
    }

    [Fact]
    public void Script_links_are_dangerous() =>
        Assert.True(LinkInspector.Inspect("javascript:alert(1)").IsSuspicious);

    [Fact]
    public void Tracking_pixels_stay_blocked_when_pictures_are_allowed()
    {
        var message = Html("<p>Hallo</p><img src=\"https://news.example.ch/logo.png\" width=\"200\" height=\"80\"><img src=\"https://t.example.net/o/abc.gif\" width=\"1\" height=\"1\">");

        var content = MessageContent.From(message, allowRemoteContent: true, blockTrackers: true);

        Assert.Equal(1, content.Trackers);
        Assert.Contains("https://news.example.ch/logo.png", content.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("t.example.net", content.Html, StringComparison.Ordinal);
        Assert.False(content.HasBlockedRemoteContent);
    }

    [Fact]
    public void Plain_text_only_shows_no_layout_and_nothing_remote()
    {
        var builder = new BodyBuilder { HtmlBody = "<h1 style=\"color:red\">Angebot</h1><img src=\"https://x.example/a.png\">", TextBody = "Angebot im Text" };
        var message = new MimeMessage { Body = builder.ToMessageBody() };

        var content = MessageContent.From(message, plainTextOnly: true);

        Assert.True(content.ShowsPlainTextOnly);
        Assert.Contains("Angebot im Text", content.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("x.example", content.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Clicked_links_are_checked_with_their_text()
    {
        var content = MessageContent.From(Html("<a href=\"https://evil.example/\" title=\"Konto\">https://www.raiffeisen.ch</a>"));

        Assert.True(content.CheckLink("https://evil.example/", clean: true).IsSuspicious);
    }

    [Fact]
    public void Hidden_links_and_text_are_found()
    {
        var scan = HtmlScan.Of("<p>Sichtbar</p><div style=\"display:none\">Versteckt <a href=\"https://x.example\">x</a></div><span style=\"font-size:0px\">auch</span><p>wieder</p>");

        Assert.True(Assert.Single(scan.Links).Hidden);
        Assert.Equal("Versteckt x auch", scan.HiddenText);
    }

    [Fact]
    public void Failed_dmarc_of_the_own_server_is_dangerous()
    {
        var message = Signed("Received: from evil by mx.provider.ch with ESMTPS; Mon, 1 Jan 2026 10:00:00 +0000",
            "Authentication-Results: mx.provider.ch; spf=fail smtp.mailfrom=evil.example; dkim=none; dmarc=fail header.from=bank.ch");

        var result = MessageAnalyzer.Analyze(message);

        Assert.Equal(ThreatLevel.Dangerous, result.Level);
        Assert.True(result.SenderForged);
    }

    [Fact]
    public void Authentication_results_written_by_the_sender_are_ignored()
    {
        // Only the sender's own (fake) header, and no server of the receiving side names it.
        var message = Signed("Received: from evil by mx.provider.ch with ESMTPS; Mon, 1 Jan 2026 10:00:00 +0000",
            "Authentication-Results: mx.bank.ch; spf=pass smtp.mailfrom=bank.ch; dkim=pass header.d=bank.ch; dmarc=pass header.from=bank.ch");

        var result = MessageAnalyzer.Analyze(message);

        Assert.Null(result.Sender.Authentication);
        Assert.DoesNotContain(result.Findings, f => f.Severity == Severity.Good);
    }

    [Fact]
    public void A_confirmed_sender_with_ordinary_content_is_unremarkable()
    {
        var message = Signed("Received: from mail.bank.ch by mx.provider.ch with ESMTPS; Mon, 1 Jan 2026 10:00:00 +0000",
            "Authentication-Results: mx.provider.ch; spf=pass smtp.mailfrom=bank.ch; dkim=pass header.d=bank.ch; dmarc=pass header.from=bank.ch");

        var result = MessageAnalyzer.Analyze(message);

        Assert.Equal(ThreatLevel.Unremarkable, result.Level);
        Assert.Equal("SPF: pass, DKIM: pass, DMARC: pass", result.Sender.Authentication);
    }

    [Fact]
    public void A_display_name_with_another_address_is_dangerous()
    {
        var message = new MimeMessage { Subject = "Konto", Body = new TextPart("plain") { Text = "Hallo" } };
        message.From.Add(new MailboxAddress("service@paypal.com", "x@evil.example"));

        Assert.Equal(ThreatLevel.Dangerous, MessageAnalyzer.Analyze(message).Level);
    }

    [Fact]
    public void A_contact_name_with_a_strange_address_is_dangerous()
    {
        var message = new MimeMessage { Subject = "Dringende Zahlung", Body = new TextPart("plain") { Text = "Bitte heute überweisen." } };
        message.From.Add(new MailboxAddress("Peter Muster", "ceo.peter@gmail.example"));
        var context = new AnalysisContext(["anna@firma.ch"], [new KnownPerson("Peter Muster", ["peter.muster@firma.ch"])]);

        var result = MessageAnalyzer.Analyze(message, context);

        Assert.Equal(ThreatLevel.Dangerous, result.Level);
    }

    [Fact]
    public void A_look_alike_of_the_own_domain_is_dangerous()
    {
        var message = new MimeMessage { Subject = "Rechnung", Body = new TextPart("plain") { Text = "Anbei" } };
        message.From.Add(new MailboxAddress("Buchhaltung", "buchhaltung@flrma.ch"));

        var result = MessageAnalyzer.Analyze(message, new AnalysisContext(["anna@firma.ch"], []));

        Assert.Equal(ThreatLevel.Dangerous, result.Level);
    }

    [Fact]
    public void A_mail_provider_is_no_look_alike_of_another()
    {
        // A contact at ymail.com: a stranger writing from gmail.com is not imitating it.
        var message = new MimeMessage { Subject = "Hallo", Body = new TextPart("plain") { Text = "Grüezi" } };
        message.From.Add(new MailboxAddress("Max", "max.meier@gmail.com"));
        var context = new AnalysisContext(["anna@firma.ch"], [new KnownPerson("Lea Keller", ["lea.keller@ymail.com"])]);

        var result = MessageAnalyzer.Analyze(message, context);

        Assert.DoesNotContain(result.Findings, f => f.Severity == Severity.High);
        Assert.NotEqual(ThreatLevel.Dangerous, result.Level);
    }

    [Fact]
    public void A_new_address_at_a_known_domain_is_no_imitation_of_a_similar_contact_domain()
    {
        // bruno@ is new, but info@ and samuel@bmtreuhand.ch are contacts – and so is someone at bdm-treuhand.ch.
        var message = new MimeMessage { Subject = "Abschluss", Body = new TextPart("plain") { Text = "Grüezi" } };
        message.From.Add(new MailboxAddress("Bruno", "bruno@bmtreuhand.ch"));
        var context = new AnalysisContext(["anna@firma.ch"],
            [new KnownPerson("BM Treuhand", ["info@bmtreuhand.ch"]), new KnownPerson("Samuel", ["samuel@bmtreuhand.ch"]), new KnownPerson("BDM", ["kontakt@bdm-treuhand.ch"])]);

        var result = MessageAnalyzer.Analyze(message, context);
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCodes.Impersonation);

        // Without a contact at bmtreuhand.ch it still looks like bdm-treuhand.ch.
        var stranger = MessageAnalyzer.Analyze(message, new AnalysisContext(["anna@firma.ch"], [new KnownPerson("BDM", ["kontakt@bdm-treuhand.ch"])]));
        Assert.Contains(stranger.Findings, f => f.Code == FindingCodes.Impersonation);
    }

    [Fact]
    public void A_contact_is_not_suspected_of_imitating_another_contact()
    {
        // Both in the address book, with domains one letter apart.
        var message = new MimeMessage { Subject = "Offerte", Body = new TextPart("plain") { Text = "Anbei" } };
        message.From.Add(new MailboxAddress("Marco", "marco@bernasconi.ch"));
        var context = new AnalysisContext(["anna@firma.ch"],
            [new KnownPerson("Marco Bernasconi", ["marco@bernasconi.ch"]), new KnownPerson("Rita Bernasconl", ["rita@bernascoli.ch"])]);

        Assert.DoesNotContain(MessageAnalyzer.Analyze(message, context).Findings, f => f.Severity == Severity.High);
    }

    [Fact]
    public void A_bank_name_with_a_strange_address_is_dangerous()
    {
        // As in a real phishing mail: sent through a hijacked mailbox of a holiday flat.
        var message = new MimeMessage { Subject = "Ihr Konto wurde eingeschränkt", Body = new TextPart("plain") { Text = "Bitte bestätigen" } };
        message.From.Add(new MailboxAddress("Raiffeisen", "raiffeisen4@a0gwcgm.ferienwohnung-niedersedlitz.de"));

        var result = MessageAnalyzer.Analyze(message, known: new PhishingList());

        Assert.Equal(ThreatLevel.Dangerous, result.Level);
        Assert.Contains(result.Findings, f => f.Title.Contains("Raiffeisen", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Raiffeisen Schweiz", "news@raiffeisen.ch", false)]
    [InlineData("Raiffeisenbank Kürten", "info@raiffeisenbank-kuerten.de", false)] // the word is part of a longer one: no claim
    [InlineData("Sparkasse KölnBonn", "service@sparkasse-koelnbonn.de", false)]
    [InlineData("PayPal", "service@paypal.ch", false)]
    [InlineData("PayPal Service", "service@paypa1-secure.example", true)]
    [InlineData("Die Post", "noreply@post-ch.example", true)]
    [InlineData("Microsoft Teams", "noreply@email.teams.microsoft.com", false)]
    [InlineData("Anna Muster", "anna@firma.example", false)]
    public void Brand_names_must_fit_the_address(string name, string address, bool deceptive)
    {
        var claimed = Brands.Claimed(name);
        Assert.Equal(deceptive, claimed is not null && !Brands.Owns(claimed, DomainNames.OfAddress(address)!));
    }

    [Theory]
    [InlineData("X-Spam-Status", "No, score=5.8 required=6.0 tests=HTML_MESSAGE", false, true, "5.8 von 6 Punkten (SpamAssassin)")]
    [InlineData("X-Spam-Status", "Yes, score=9.1 required=5.0 tests=BAYES_99", true, false, "9.1 von 5 Punkten (SpamAssassin)")]
    [InlineData("X-Spamd-Result", "default: False [2.10 / 15.00]; R_SPF_ALLOW(-0.20)", false, false, "2.1 von 15 Punkten (Rspamd)")]
    [InlineData("X-SASI-SpamProbability", "10%", false, false, "10 % Spamwahrscheinlichkeit (Sophos)")]
    [InlineData("X-Forefront-Antispam-Report", "CIP:1.2.3.4;CTRY:CH;LANG:de;SCL:6;SRV:;", true, false, "Spam-Stufe 6 von 9")]
    public void The_spam_rating_of_the_mail_server_is_read(string header, string value, bool spam, bool close, string text)
    {
        var message = new MimeMessage { Subject = "Hallo", Body = new TextPart("plain") { Text = "Text" } };
        message.From.Add(new MailboxAddress("X", "x@example.com"));
        message.Headers.Add(header, value);

        var rating = SpamRatings.Of(message);

        Assert.NotNull(rating);
        Assert.Equal((spam, close), (rating.IsSpam, rating.IsClose));
        Assert.StartsWith(text, rating.Text, StringComparison.Ordinal);
        var findings = MessageAnalyzer.Analyze(message, known: new PhishingList()).Findings;
        Assert.Equal(spam, findings.Any(f => f.Severity == Severity.Medium && f.Title.Contains("Spam", StringComparison.Ordinal)));
    }

    [Fact]
    public void Reply_to_elsewhere_and_program_attachments_are_noticed()
    {
        var body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Rechnung anbei" },
            new MimePart("application", "octet-stream") { Content = new MimeContent(new MemoryStream([1])), ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "Rechnung.pdf.exe" } },
        };
        var message = new MimeMessage { Subject = "Rechnung", Body = body };
        message.From.Add(new MailboxAddress("Firma", "info@firma.ch"));
        message.ReplyTo.Add(new MailboxAddress("Firma", "firma.ch@mail.example"));

        var result = MessageAnalyzer.Analyze(message);

        Assert.Equal(ThreatLevel.Dangerous, result.Level);
        Assert.Contains(result.Findings, f => f.Severity == Severity.Medium);
    }

    [Fact]
    public void Message_ids_name_the_sender_domain_not_the_computer()
    {
        var message = MessageComposer.Build(new MailboxAddress("Anna", "anna@firma.ch"), new ComposeDraft("bob@example.ch", string.Empty, "Hallo", "Hallo", null, [], []), []);

        Assert.EndsWith("@firma.ch", message.MessageId, StringComparison.Ordinal);
    }

    [Fact]
    public void Hiding_the_time_zone_sends_utc()
    {
        var message = new MimeMessage { Date = new DateTimeOffset(2026, 10, 16, 14, 0, 0, TimeSpan.FromHours(2)) };
        message.From.Add(new MailboxAddress("Anna", "anna@firma.ch"));

        OutgoingMail.Prepare(message, hideTimeZone: true);

        Assert.Equal(TimeSpan.Zero, message.Date.Offset);
        Assert.Equal(12, message.Date.Hour);
    }

    [Fact]
    public async Task Privacy_options_round_trip()
    {
        var store = new MemorySettings();
        var options = new PrivacyOptions { RemoteContent = RemoteContentPolicy.Block, TrustedSenders = ["@firma.ch"], ConfirmLinks = LinkConfirmation.Always, PlainText = true, BlockTrackers = false };

        await options.SaveAsync(store, TestContext.Current.CancellationToken);
        var loaded = await PrivacyOptions.LoadAsync(store, TestContext.Current.CancellationToken);

        Assert.Equal(RemoteContentPolicy.Block, loaded.RemoteContent);
        Assert.Equal(LinkConfirmation.Always, loaded.ConfirmLinks);
        Assert.True(loaded.PlainText);
        Assert.False(loaded.BlockTrackers);
        Assert.True(loaded.IsTrusted(new MailboxAddress("x", "x@firma.ch")));
        Assert.False(loaded.LoadsRemoteContent(new MailboxAddress("x", "x@firma.ch"), loadedBefore: true));
    }

    [Theory]
    [InlineData("Anna@Firma.ch", "anna@firma.ch")]
    [InlineData(" firma.ch ", "@firma.ch")]
    [InlineData("@Firma.CH", "@firma.ch")]
    [InlineData("<news@shop.example.co.uk>", "news@shop.example.co.uk")]
    [InlineData("hallo", null)]
    [InlineData("anna @firma.ch", null)]
    [InlineData("a@b@firma.ch", null)]
    [InlineData("", null)]
    public void Trusted_entries_are_addresses_or_domains(string input, string? expected) =>
        Assert.Equal(expected, PrivacyOptions.TrustedEntry(input));

    [Fact]
    public void Trusted_senders_load_pictures_unless_forged()
    {
        var options = new PrivacyOptions { TrustedSenders = ["anna@firma.ch"] };
        var anna = new MailboxAddress("Anna", "anna@firma.ch");

        Assert.True(options.LoadsRemoteContent(anna, loadedBefore: false));
        Assert.False(options.LoadsRemoteContent(anna, loadedBefore: false, senderForged: true));
        Assert.False(options.LoadsRemoteContent(new MailboxAddress("Bob", "bob@x.ch"), loadedBefore: false));
    }

    private static MimeMessage Html(string html) => new() { Body = new TextPart("html") { Text = html } };

    private static MimeMessage Signed(string received, string authentication)
    {
        var raw = $"{received}\r\n{authentication}\r\nFrom: Bank <info@bank.ch>\r\nTo: anna@provider.ch\r\nSubject: Ihr Konto\r\nMessage-ID: <1@bank.ch>\r\nContent-Type: text/plain\r\n\r\nGuten Tag\r\n";
        return MimeMessage.Load(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(raw)));
    }

    [Fact]
    public async Task The_statistics_count_each_mail_once()
    {
        using var statistics = new PrivacyStatistics(new MemorySettings());
        var message = new MimeMessage { Subject = "Konto", Body = new TextPart("plain") { Text = "Hallo" } };
        message.From.Add(new MailboxAddress("Raiffeisen", "raiffeisen4@ferienwohnung.example"));
        var assessment = MessageAnalyzer.Analyze(message, known: new PhishingList());

        await statistics.RecordMailAsync("<1@example>", assessment, trackers: 2, imagesBlocked: true, TestContext.Current.CancellationToken);
        await statistics.RecordMailAsync("<1@example>", assessment, trackers: 2, imagesBlocked: true, TestContext.Current.CancellationToken); // opened again
        await statistics.RecordLinkAsync(warned: true, cleaned: true, TestContext.Current.CancellationToken);
        var counts = await statistics.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal((1, 1, 1, 2, 1, 1, 1), (counts.Checked, counts.Dangerous, counts.Impersonations, counts.TrackersBlocked, counts.ImagesBlocked, counts.LinksWarned, counts.LinksCleaned));
        await statistics.ResetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, (await statistics.GetAsync(TestContext.Current.CancellationToken)).Checked);
    }

    [Fact]
    public void File_links_open_only_for_safe_senders_that_pass_the_check()
    {
        var options = new PrivacyOptions { FileLinkSenders = ["@firma.ch", "lea@example.com"] };

        Assert.True(options.OpensFileLinks(new MailboxAddress("Marco", "marco@firma.ch")));
        Assert.True(options.OpensFileLinks(new MailboxAddress("Lea", "Lea@Example.com")));
        Assert.False(options.OpensFileLinks(new MailboxAddress("Nadia", "nadia@example.com")));
        Assert.False(options.OpensFileLinks(new MailboxAddress("Marco", "marco@firma.ch"), senderForged: true));
        Assert.False(options.IsTrusted(new MailboxAddress("Marco", "marco@firma.ch"))); // pictures are a separate list
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));

        public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
        {
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(_values);
    }
}
