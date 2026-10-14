using Neruna.Storage;

namespace Neruna.Client.Tests;

public sealed class DataFolderTests
{
    [Fact]
    public void The_data_moves_out_of_the_installers_folder_and_the_installers_files_stay()
    {
        var root = Path.Combine(Path.GetTempPath(), "neruna-tests", Guid.NewGuid().ToString("N"));
        var old = Path.Combine(root, "Neruna");
        var data = Path.Combine(root, "NerunaData");
        try
        {
            Directory.CreateDirectory(Path.Combine(old, "messages", "x"));
            Directory.CreateDirectory(Path.Combine(old, "current"));
            File.WriteAllText(Path.Combine(old, "neruna.db"), "db");
            File.WriteAllText(Path.Combine(old, "messages", "x", "1.eml"), "mail");
            File.WriteAllText(Path.Combine(old, "language"), "fr");
            File.WriteAllText(Path.Combine(old, "Update.exe"), "installer");

            Assert.True(DataFolder.MoveOnce(old, data));

            Assert.Equal("db", File.ReadAllText(Path.Combine(data, "neruna.db")));
            Assert.Equal("mail", File.ReadAllText(Path.Combine(data, "messages", "x", "1.eml")));
            Assert.Equal("fr", File.ReadAllText(Path.Combine(data, "language")));
            Assert.False(File.Exists(Path.Combine(old, "neruna.db")));
            Assert.True(File.Exists(Path.Combine(old, "Update.exe")));
            Assert.True(Directory.Exists(Path.Combine(old, "current")));

            // Once moved, nothing happens any more (a new old database is not taken over).
            File.WriteAllText(Path.Combine(old, "neruna.db"), "other");
            Assert.True(DataFolder.MoveOnce(old, data));
            Assert.Equal("db", File.ReadAllText(Path.Combine(data, "neruna.db")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
