using System.Diagnostics;
using System.Globalization;

namespace Neruna.Core.Security;

/// <summary>
/// Attachments opened from a mail are the classic way in for attackers. Before one goes to the system's default app:
/// programs and scripts need a confirmation, the file is marked as coming from the internet (so the system's own
/// protection – SmartScreen, Office's protected view, Gatekeeper – applies), and copies are removed after a day
/// (decrypted S/MIME attachments must not pile up in the temp folder).
/// </summary>
public static class OpenedAttachments
{
    // Programs, scripts, shortcuts, installers and disk images (these used to slip past the "from the internet" mark).
    private static readonly HashSet<string> Risky = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".msi", ".msp", ".msix", ".msixbundle", ".appx", ".appxbundle",
        ".appref-ms", ".application", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".wsc",
        ".hta", ".cpl", ".msc", ".lnk", ".url", ".scf", ".reg", ".inf", ".chm", ".gadget", ".settingcontent-ms",
        ".library-ms", ".search-ms", ".xll", ".jar", ".jnlp",
        ".iso", ".img", ".vhd", ".vhdx",
        // macOS
        ".app", ".pkg", ".mpkg", ".dmg", ".command", ".terminal", ".workflow", ".scpt", ".applescript",
        // Linux
        ".sh", ".run", ".bin", ".desktop", ".appimage", ".deb", ".rpm",
        // Office add-ins
        ".ppam", ".xlam",
    };

    /// <summary>The folder the opened copies live in (one subfolder per file).</summary>
    public static string Root => Path.Combine(Path.GetTempPath(), "Neruna");

    /// <summary>A program, script or similar: the user confirms before it is opened.</summary>
    public static bool IsRisky(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        // Windows ignores trailing dots and spaces ("rechnung.exe. " runs as .exe).
        return Risky.Contains(Path.GetExtension(CleanName(fileName).TrimEnd('.', ' ')));
    }

    /// <summary>
    /// The name as it really is: invisible format characters removed. "rechnung‮fdp.exe" (with a right-to-left
    /// override) would otherwise show as "rechnungexe.pdf".
    /// </summary>
    public static string CleanName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return string.Concat(fileName.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)).Trim();
    }

    /// <summary>A new, private folder for one opened attachment.</summary>
    public static string CreateFolder()
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N")[..12]);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path); // the user's temp folder is private already
        }
        else
        {
            const UnixFileMode privateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(Root, privateDir);
            File.SetUnixFileMode(Root, privateDir); // /tmp is shared: other users must not read the copies
            Directory.CreateDirectory(path, privateDir);
        }

        return path;
    }

    /// <summary>
    /// Marks the file as downloaded from the internet: on Windows the Zone.Identifier stream (zone 3), on macOS the
    /// quarantine attribute. Linux has no such mark. Best effort – a file system without streams just stays unmarked.
    /// </summary>
    public static void MarkAsFromInternet(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(path + ":Zone.Identifier", ZoneIdentifier);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var value = string.Create(CultureInfo.InvariantCulture, $"0083;{DateTimeOffset.UtcNow.ToUnixTimeSeconds():x};Neruna;");
                using var process = Process.Start(new ProcessStartInfo("/usr/bin/xattr") { ArgumentList = { "-w", "com.apple.quarantine", value, path }, UseShellExecute = false });
                process?.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Not marked: the confirmation for risky types still applies.
        }
    }

    /// <summary>Zone 3 = internet: SmartScreen checks programs, Office opens documents in protected view.</summary>
    public const string ZoneIdentifier = "[ZoneTransfer]\r\nZoneId=3\r\n";

    /// <summary>Removes copies older than <paramref name="maxAge"/> (still open in another program: tried next time).</summary>
    /// <returns>How many folders were removed.</returns>
    public static int CleanUp(TimeSpan maxAge, DateTime? now = null, string? root = null)
    {
        root ??= Root;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var limit = (now ?? DateTime.UtcNow) - maxAge;
        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(folder) < limit)
                {
                    Directory.Delete(folder, recursive: true);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use – next start.
            }
        }

        return removed;
    }
}
