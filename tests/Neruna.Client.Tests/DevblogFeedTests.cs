using Neruna.Core.News;

namespace Neruna.Client.Tests;

public class DevblogFeedTests
{
    [Fact]
    public void The_devblog_feed_becomes_posts_newest_first()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom" xml:lang="de-CH">
              <id>https://neruna.org/de/blog/</id>
              <title>Neruna Devblog</title>
              <entry>
                <id>https://neruna.org/de/blog/hallo-devblog/</id>
                <link href="https://neruna.org/de/blog/hallo-devblog/"/>
                <title type="html">Hallo Devblog!</title>
                <updated>2026-10-16T12:00:00Z</updated>
                <summary type="html">Ab jetzt erzählen wir hier &amp;amp; dort.</summary>
                <content type="html">&lt;p&gt;Erster Absatz.&lt;/p&gt;&lt;h2&gt;Was kommt&lt;/h2&gt;&lt;ul&gt;&lt;li&gt;Eins&lt;/li&gt;&lt;li&gt;Zwei&lt;/li&gt;&lt;/ul&gt;</content>
              </entry>
              <entry>
                <id>https://neruna.org/de/blog/neruna-0-11-0/</id>
                <link href="https://neruna.org/de/blog/neruna-0-11-0/"/>
                <title type="html">Neruna 0.11: Workspaces</title>
                <updated>2026-10-17T12:00:00Z</updated>
                <summary type="html">Business am Tag, Privat am Abend.</summary>
                <content type="html">&lt;p&gt;Text&lt;/p&gt;</content>
              </entry>
              <entry>
                <title>Ohne Link – ausgelassen</title>
              </entry>
              <entry>
                <link href="http://example.com/"/>
                <title>Nicht https – ausgelassen</title>
              </entry>
            </feed>
            """;

        var posts = DevblogFeed.Parse(xml);

        Assert.Equal(["Neruna 0.11: Workspaces", "Hallo Devblog!"], posts.Select(p => p.Title));
        var hello = posts[1];
        Assert.Equal(new DateOnly(2026, 10, 16), hello.Date);
        Assert.Equal("Ab jetzt erzählen wir hier & dort.", hello.Summary);
        Assert.Equal(
            [new(NewsBlockKind.Paragraph, "Erster Absatz."), new(NewsBlockKind.Heading, "Was kommt"), new(NewsBlockKind.Item, "Eins"), new NewsBlock(NewsBlockKind.Item, "Zwei")],
            hello.Blocks);
        Assert.Equal(new Uri("https://neruna.org/de/blog/hallo-devblog/"), hello.Url);
        Assert.Equal(new Uri("https://neruna.org/it/blog/feed.xml"), DevblogFeed.FeedUri("it"));

        // Screenshots with their captions stay on the web.
        Assert.Equal([new NewsBlock(NewsBlockKind.Paragraph, "Vorher"), new NewsBlock(NewsBlockKind.Paragraph, "Nachher")],
            DevblogFeed.Blocks("<p>Vorher</p><figure class=\"frame\"><img src=\"x.webp\" alt=\"Bild\"><figcaption>Bildtext</figcaption></figure><p>Nachher</p>"));
        Assert.Equal(new Uri("https://neruna.org/en/blog/feed.xml"), DevblogFeed.FeedUri("rm"));
    }
}
