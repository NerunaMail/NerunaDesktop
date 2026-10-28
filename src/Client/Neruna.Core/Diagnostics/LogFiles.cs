namespace Neruna.Core.Diagnostics;

/// <summary>The log folder does not grow without end: what was last written more than <see cref="KeepDays"/> days ago goes.</summary>
public static class LogFiles
{
    public const int KeepDays = 14;

    /// <summary>Old logs – the day files and the protocol logs below – deleted at the start; other files stay.</summary>
    public static void DeleteOld(string directory, DateTime now)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var limit = now.Date.AddDays(-KeepDays);
        foreach (var file in Directory.EnumerateFiles(directory, "*.log", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTime(file) < limit)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file still open elsewhere stays until next time.
            }
        }
    }
}
