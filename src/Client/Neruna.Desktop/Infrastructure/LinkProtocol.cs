using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// Registers "neruna:" links with the system, so the button in the mail with the connection code opens Neruna
/// (<see cref="Neruna.Core.Cloud.CloudConnectLink"/>). Unlike mailto: no question of being the default – the scheme is
/// Neruna's own. At every start (cheap, and it follows a moved or updated program); macOS declares it in the
/// Info.plist (scripts/publish.sh). Not in debug builds: they would point the links to a build folder.
/// </summary>
internal static class LinkProtocol
{
    private const string Scheme = "neruna";

    public static void Register()
    {
#if !DEBUG
        try
        {
            if (OperatingSystem.IsWindows())
            {
                RegisterWindows();
            }
            else if (OperatingSystem.IsLinux())
            {
                RegisterLinux();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Links then open nothing; the code can still be typed in.
        }
#endif
    }

    /// <summary>On uninstall (Windows; elsewhere the files go with the app or point nowhere).</summary>
    public static void Unregister()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Scheme, throwOnMissingSubKey: false);
        }
    }

    private static string Executable => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath!;

    [SupportedOSPlatform("windows")]
    private static void RegisterWindows()
    {
        var command = $"\"{Executable}\" \"%1\"";
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme);
        using var open = key.CreateSubKey(@"shell\open\command");
        if (open.GetValue(string.Empty) as string == command)
        {
            return;
        }

        key.SetValue(string.Empty, "URL:Neruna");
        key.SetValue("URL Protocol", string.Empty);
        key.CreateSubKey("DefaultIcon").SetValue(string.Empty, $"\"{Executable}\",0");
        open.SetValue(string.Empty, command);
    }

    // A hidden .desktop file only for the scheme (the visible one belongs to "Standard-Mailprogramm"), made the handler
    // with xdg-mime once – and again when the program moved.
    [SupportedOSPlatform("linux")]
    private static void RegisterLinux()
    {
        const string file = "neruna-links.desktop";
        var applications = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "applications");
        var exec = Executable.Contains(' ', StringComparison.Ordinal) ? $"\"{Executable}\"" : Executable;
        var content = $"""
            [Desktop Entry]
            Type=Application
            Name=Neruna
            Exec={exec} %u
            NoDisplay=true
            Terminal=false
            MimeType=x-scheme-handler/{Scheme};

            """;
        var path = Path.Combine(applications, file);
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return;
        }

        Directory.CreateDirectory(applications);
        File.WriteAllText(path, content);
        _ = Task.Run(() =>
        {
            Run("xdg-mime", "default", file, "x-scheme-handler/" + Scheme);
            Run("update-desktop-database", applications);
        });
    }

    private static void Run(string file, params string[] args)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Tool missing.
        }
    }
}
