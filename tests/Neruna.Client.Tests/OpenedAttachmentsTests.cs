using MimeKit;
using Neruna.Core.Mail;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

/// <summary>Opening attachments: programs need a confirmation, disguised names are unmasked, old copies go away.</summary>
public class OpenedAttachmentsTests
{
    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("Rechnung.pdf.exe", true)]
    [InlineData("rechnung.EXE. ", true)]
    [InlineData("skript.js", true)]
    [InlineData("Verknüpfung.lnk", true)]
    [InlineData("image.iso", true)]
    [InlineData("installer.dmg", true)]
    [InlineData("rechnung‮fdp.exe", true)] // shows as "rechnungexe.pdf"
    [InlineData("Rechnung.pdf", false)]
    [InlineData("Offerte.docx", false)]
    [InlineData("Foto.JPG", false)]
    [InlineData("ohne-endung", false)]
    public void Programs_and_scripts_are_risky(string name, bool risky) => Assert.Equal(risky, OpenedAttachments.IsRisky(name));

    [Fact]
    public void Attachment_names_lose_invisible_characters_that_disguise_the_type()
    {
        var part = new MimePart("application", "octet-stream") { FileName = "rechnung‮fdp.exe" };
        Assert.Equal("rechnungfdp.exe", MessageContent.FileNameOf(part));
    }

    [Fact]
    public void Copies_older_than_a_day_are_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), "neruna-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var old = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
            File.WriteAllText(Path.Combine(old, "a.pdf"), "x");
            Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
            var fresh = Directory.CreateDirectory(Path.Combine(root, "fresh")).FullName;

            Assert.Equal(1, OpenedAttachments.CleanUp(TimeSpan.FromDays(1), root: root));
            Assert.False(Directory.Exists(old));
            Assert.True(Directory.Exists(fresh));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Copies_live_in_a_folder_only_the_user_can_read()
    {
        var folder = OpenedAttachments.CreateFolder();
        try
        {
            Assert.StartsWith(OpenedAttachments.Root, folder, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(OpenedAttachments.Root));
            }
        }
        finally
        {
            Directory.Delete(folder);
        }
    }
}
