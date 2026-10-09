using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Neruna.Desktop;

/// <summary>
/// The chosen language also as a small file next to the database: read at the very start, before the database is open
/// (splash screen). The setting <c>ui.language</c> stays the source (and goes into the backup).
/// </summary>
internal static class LanguageFile
{
    private const string Name = "language";

    public static string? Read(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, Name);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string dataDirectory, string language)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, Name), language);
        }
        catch (IOException)
        {
            // Only the splash screen would show the old language once more.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Starts Neruna again (same arguments) and closes this instance – for a new language.</summary>
    public static void Restart()
    {
        if (Environment.ProcessPath is { } path)
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, Arguments = string.Join(' ', Environment.GetCommandLineArgs().Skip(1).Select(a => $"\"{a}\"")) });
        }

        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
