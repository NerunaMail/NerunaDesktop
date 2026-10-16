using System.Globalization;
using System.Xml.Linq;
using Neruna.Core.Mail;

namespace Neruna.Core.News;

/// <summary>A devblog post: title, date, teaser, the text as blocks and its page on neruna.org.</summary>
public sealed record NewsPost(string Title, DateOnly Date, string Summary, IReadOnlyList<NewsBlock> Blocks, Uri Url)
{
    /// <summary>The whole text, one block per line.</summary>
    public string Text => string.Join("\n", Blocks.Select(b => b.Kind == NewsBlockKind.Item ? "• " + b.Text : b.Text));
}

public enum NewsBlockKind
{
    Paragraph,
    Heading,
    Item,
}

/// <summary>A paragraph, a heading or a list item of a post – enough to read it in the app; pictures stay on the web.</summary>
public sealed record NewsBlock(NewsBlockKind Kind, string Text);

/// <summary>
/// Einstellungen → News: the devblog of neruna.org (its Atom feed, in the app's language). Read only when the page is
/// opened; nothing is sent but the request for the feed.
/// </summary>
public sealed class DevblogFeed(HttpClient http)
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    public static Uri FeedUri(string language) =>
        new($"https://neruna.org/{(language is "de" or "en" or "fr" or "it" ? language : "en")}/blog/feed.xml");

    public async Task<IReadOnlyList<NewsPost>> GetAsync(string language, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(FeedUri(language), cancellationToken);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>The posts of an Atom feed, newest first; entries without title or link are left out.</summary>
    public static IReadOnlyList<NewsPost> Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var document = XDocument.Parse(xml, LoadOptions.None);
        var posts = new List<NewsPost>();
        foreach (var entry in document.Root?.Elements(Atom + "entry") ?? [])
        {
            var title = entry.Element(Atom + "title")?.Value;
            var link = entry.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(link, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            var updated = entry.Element(Atom + "updated")?.Value ?? entry.Element(Atom + "published")?.Value;
            var date = DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? DateOnly.FromDateTime(when.UtcDateTime)
                : DateOnly.MinValue;
            posts.Add(new NewsPost(
                HtmlText.ToPlainText(title).Trim(),
                date,
                HtmlText.ToPlainText(entry.Element(Atom + "summary")?.Value ?? string.Empty).Trim(),
                Blocks(entry.Element(Atom + "content")?.Value ?? string.Empty),
                url));
        }

        return [.. posts.OrderByDescending(p => p.Date)];
    }

    /// <summary>
    /// The post's HTML as blocks: headings, paragraphs and list items; figures (screenshot and caption) are left out,
    /// the app shows no pictures from the web.
    /// </summary>
    public static IReadOnlyList<NewsBlock> Blocks(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var blocks = new List<NewsBlock>();
        var text = new System.Text.StringBuilder();
        var kind = NewsBlockKind.Paragraph;
        var skip = 0;

        void Flush()
        {
            var value = System.Text.RegularExpressions.Regex.Replace(text.ToString(), @"\s+", " ").Trim();
            if (value.Length > 0)
            {
                blocks.Add(new NewsBlock(kind, value));
            }

            text.Clear();
            kind = NewsBlockKind.Paragraph;
        }

        var tokenizer = new MimeKit.Text.HtmlTokenizer(new StringReader(html)) { DecodeCharacterReferences = true };
        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case MimeKit.Text.HtmlTagToken { Name: "figure" or "script" or "style" } tag:
                    skip += tag.IsEndTag ? -1 : tag.IsEmptyElement ? 0 : 1;
                    break;
                case MimeKit.Text.HtmlTagToken { Name: "h1" or "h2" or "h3" or "h4" } tag when skip == 0:
                    Flush();
                    kind = tag.IsEndTag ? NewsBlockKind.Paragraph : NewsBlockKind.Heading;
                    break;
                case MimeKit.Text.HtmlTagToken { Name: "li" } tag when skip == 0:
                    Flush();
                    kind = tag.IsEndTag ? NewsBlockKind.Paragraph : NewsBlockKind.Item;
                    break;
                case MimeKit.Text.HtmlTagToken { Name: "p" or "div" or "ul" or "ol" or "br" or "blockquote" or "pre" } when skip == 0:
                    Flush();
                    break;
                case MimeKit.Text.HtmlDataToken data when skip == 0:
                    text.Append(data.Data);
                    break;
            }
        }

        Flush();
        return blocks;
    }
}
