using System.Text;
using MimeKit;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class SignatureImportTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public void Html_of_another_program_keeps_its_layout_and_takes_the_pictures_next_to_it()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var html = "<html><head><meta http-equiv=Content-Type content=\"text/html; charset=windows-1252\"><style>p.MsoNormal{margin:0cm}</style></head>"
                   + "<body><p class=MsoNormal><b>Anna Müller</b> – Example AG</p><img src=\"Standard-Dateien/image001.png\" width=80>"
                   + "<script>alert(1)</script><a href=\"https://example.com\" onclick=\"x()\">example.com</a></body></html>";
        var asked = new List<string>();

        var result = SignatureImport.Read("Standard.htm", Encoding.GetEncoding(1252).GetBytes(html), path =>
        {
            asked.Add(path);
            return path == "Standard-Dateien/image001.png" ? Png : null;
        });

        Assert.Contains("Anna Müller</b> – Example AG", result, StringComparison.Ordinal);
        Assert.Contains("p.MsoNormal{margin:0cm}", result, StringComparison.Ordinal);
        Assert.Contains("src=\"data:image/png;base64,", result, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<body", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Standard-Dateien/image001.png"], asked);
    }

    [Fact]
    public void Text_becomes_lines_with_clickable_links()
    {
        var result = SignatureImport.Read("kurz.txt", Encoding.UTF8.GetBytes("Anna Müller\r\nwww.example.com\r\n"));

        Assert.Equal("<div class=\"neruna-text\"><div>Anna Müller</div><div><a href=\"https://www.example.com\">www.example.com</a></div></div>", result);
    }

    [Fact]
    public void A_mail_gives_its_body_with_embedded_pictures()
    {
        var builder = new BodyBuilder();
        var logo = builder.LinkedResources.Add("logo.png", Png);
        logo.ContentId = "logo@example";
        builder.HtmlBody = "<p>Freundliche Grüsse<br>Anna</p><img src=\"cid:logo@example\">";
        var message = new MimeMessage { Subject = "Signatur", Body = builder.ToMessageBody() };
        using var stream = new MemoryStream();
        message.WriteTo(stream, TestContext.Current.CancellationToken);

        var result = SignatureImport.Read("Signatur.eml", stream.ToArray());

        Assert.Contains("Freundliche Grüsse<br/>Anna", result, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", result, StringComparison.Ordinal);
        Assert.DoesNotContain("cid:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_and_empty_files_are_refused_with_a_reason()
    {
        Assert.Throws<InvalidDataException>(() => SignatureImport.Read("logo.png", Png));
        Assert.Throws<InvalidDataException>(() => SignatureImport.Read("leer.html", Encoding.UTF8.GetBytes("<html><body> <p></p></body></html>")));
    }

    [Fact]
    public async Task Pictures_are_only_taken_from_the_folder_of_the_file()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "sig"));
            Directory.CreateDirectory(Path.Combine(folder.FullName, "Standard-Dateien"));
            await File.WriteAllBytesAsync(Path.Combine(folder.FullName, "Standard-Dateien", "logo.png"), Png, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, "geheim.png"), Png, TestContext.Current.CancellationToken);
            var path = Path.Combine(folder.FullName, "Standard.htm");
            await File.WriteAllTextAsync(path, "<p>Anna</p><img src=\"Standard-Dateien/logo.png\"><img src=\"../geheim.png\">", TestContext.Current.CancellationToken);

            var result = await SignatureImport.ReadFileAsync(path, TestContext.Current.CancellationToken);

            Assert.Single(result.Split("data:image/png").Skip(1));
            Assert.Contains("../geheim.png", result, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
