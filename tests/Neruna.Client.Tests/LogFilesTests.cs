using Neruna.Core.Diagnostics;

namespace Neruna.Client.Tests;

/// <summary>The log folder does not grow without end: logs older than two weeks go at the start.</summary>
public class LogFilesTests
{
    [Fact]
    public void Old_logs_and_protocol_logs_are_deleted_recent_ones_stay()
    {
        var directory = Path.Combine(Path.GetTempPath(), "neruna-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "protocol"));
        var now = new DateTime(2026, 10, 28, 9, 0, 0);
        string Log(string name, int daysAgo)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTime(path, now.AddDays(-daysAgo));
            return path;
        }

        var old = Log("neruna-20261001.log", 27);
        var oldProtocol = Log(Path.Combine("protocol", "imap-1a2b3c4d-20261001-101010-000-1.log"), 27);
        var recent = Log("neruna-20261020.log", 8);
        var other = Log("notes.txt", 40);
        try
        {
            LogFiles.DeleteOld(directory, now);

            Assert.False(File.Exists(old));
            Assert.False(File.Exists(oldProtocol));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(other));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
