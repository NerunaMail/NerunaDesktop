using System.Text;
using MimeKit;
using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class MapiRequestTests
{
    // Written the way native/mapi/neruna_mapi.c writes it: %TEMP%\Neruna-MAPI\<id>\mail.nerunamapi, attachments in 0\, 1\ …
    private static (string Root, string Request) Request(string json, params (string Name, byte[] Data)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "neruna-test-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, MapiRequest.FolderName, "4711-1-0");
        for (var i = 0; i < files.Length; i++)
        {
            Directory.CreateDirectory(Path.Combine(folder, i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            File.WriteAllBytes(Path.Combine(folder, i.ToString(System.Globalization.CultureInfo.InvariantCulture), files[i].Name), files[i].Data);
        }

        Directory.CreateDirectory(folder);
        var request = Path.Combine(folder, "mail" + MapiRequest.Extension);
        File.WriteAllText(request, json.Replace("{folder}", folder.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal), new UTF8Encoding(false));
        return (root, request);
    }

    [Fact]
    public async Task A_mail_from_another_program_becomes_a_new_mail_with_its_attachments_and_the_copies_are_removed()
    {
        var sep = Path.DirectorySeparatorChar == '\\' ? "\\\\" : "/";
        var (root, request) = Request(
            "{\"subject\":\"Rechnung 2026-117\",\"body\":\"Guten Tag\\r\\n\\r\\nanbei die Rechnung.\",\"dialog\":true,"
            + "\"recipients\":[{\"kind\":\"to\",\"name\":\"Anna Muster\",\"address\":\"SMTP:anna@example.com\"},"
            + "{\"kind\":\"to\",\"name\":\"lea@example.com\",\"address\":\"\"},"
            + "{\"kind\":\"cc\",\"name\":\"\",\"address\":\"marco@example.com\"},"
            + "{\"kind\":\"bcc\",\"name\":\"Buchhaltung\",\"address\":\"buchhaltung@example.com\"},"
            + "{\"kind\":\"to\",\"name\":\"Nur ein Name\",\"address\":\"\"}],"
            + "\"attachments\":[{\"path\":\"{folder}" + sep + "0" + sep + "Rechnung 2026-117.pdf\",\"name\":\"Rechnung 2026-117.pdf\"}]}",
            ("Rechnung 2026-117.pdf", "%PDF-1.4 test"u8.ToArray()));
        try
        {
            Assert.True(MapiRequest.IsRequest(request));

            var draft = await MapiRequest.ReadAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal("\"Anna Muster\" <anna@example.com>, lea@example.com", draft.To);
            Assert.Equal("marco@example.com", draft.Cc);
            Assert.Equal("\"Buchhaltung\" <buchhaltung@example.com>", draft.Bcc);
            Assert.Equal("Rechnung 2026-117", draft.Subject);
            Assert.Equal("Guten Tag\n\nanbei die Rechnung.", draft.Body);
            var attachment = Assert.IsType<MimePart>(Assert.Single(draft.Attachments));
            Assert.Equal("Rechnung 2026-117.pdf", attachment.FileName);
            Assert.Equal("application/pdf", attachment.ContentType.MimeType);
            Assert.False(Directory.Exists(Path.GetDirectoryName(request)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Only_the_dlls_own_copies_are_attached_and_only_its_folder_is_taken()
    {
        var outside = Path.Combine(Path.GetTempPath(), "neruna-test-secret-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "geheim");
        var (root, request) = Request("{\"subject\":\"x\",\"attachments\":[{\"path\":\"" + outside.Replace("\\", "\\\\", StringComparison.Ordinal) + "\",\"name\":\"a.txt\"}]}");
        try
        {
            Assert.False(MapiRequest.IsRequest(outside.Replace(".txt", MapiRequest.Extension, StringComparison.Ordinal)));

            var draft = await MapiRequest.ReadAsync(request, TestContext.Current.CancellationToken);

            Assert.Empty(draft.Attachments);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
            Directory.Delete(root, recursive: true);
        }
    }
}
