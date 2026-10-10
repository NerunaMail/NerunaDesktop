using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Neruna.Desktop.Infrastructure;

/// <summary>Whether Neruna opens mailto: links and .eml files on this computer.</summary>
internal sealed record DefaultMailStatus(bool Mailto, bool Eml)
{
    public bool IsDefault => Mailto && Eml;
}

/// <summary>
/// "Neruna als Standard-Mailprogramm": registers Neruna for mailto: and .eml with the operating system and checks
/// whether it is the default. Windows lets only the user choose the default (the button opens the settings page);
/// Linux (xdg-mime) and macOS (Launch Services, with a confirmation by the system) set it directly.
/// </summary>
internal abstract class DefaultMailApp
{
    public static DefaultMailApp? Current { get; } =
        OperatingSystem.IsWindows() ? new WindowsDefaultMailApp()
        : OperatingSystem.IsMacOS() ? new MacDefaultMailApp()
        : OperatingSystem.IsLinux() ? new LinuxDefaultMailApp()
        : null;

    public abstract DefaultMailStatus GetStatus();

    /// <summary>Registers Neruna and makes it the default where the system allows that.</summary>
    /// <returns>True when the user still has to choose Neruna in the system settings (Windows; they are opened).</returns>
    public abstract bool MakeDefault();

    /// <summary>At start: an earlier registration points to this executable again (moved, updated).</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>On uninstall: the registration goes (Windows; the others are removed with the app).</summary>
    public virtual void Unregister()
    {
    }

    protected static string Executable => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath!;

    protected static string Run(string file, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return string.Empty; // tool missing
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsDefaultMailApp : DefaultMailApp
{
    private const string AppName = "Neruna";
    private const string UrlProgId = "Neruna.Url.mailto";
    private const string EmlProgId = "Neruna.eml";
    private const string ClientKey = @"Software\Clients\Mail\Neruna";

    public override DefaultMailStatus GetStatus() => new(
        UserChoice(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\mailto\UserChoice") == UrlProgId,
        UserChoice(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.eml\UserChoice") == EmlProgId);

    public override bool MakeDefault()
    {
        Register();
        // Windows 10/11 protect the default apps: the user chooses there. "registeredAppUser" opens Neruna's page.
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + AppName) { UseShellExecute = true });
        return true;
    }

    public override void Refresh()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ClientKey);
        if (key is not null)
        {
            Register();
        }
    }

    public override void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + UrlProgId, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + EmlProgId, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ClientKey, throwOnMissingSubKey: false);
        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
        {
            registered?.DeleteValue(AppName, throwOnMissingValue: false);
        }

        using (var openWith = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.eml\OpenWithProgids", writable: true))
        {
            openWith?.DeleteValue(EmlProgId, throwOnMissingValue: false);
        }

        NotifyShell();
    }

    // Per user (no administrator rights): the two ProgIDs, the capabilities and the entry in RegisteredApplications.
    private static void Register()
    {
        var command = $"\"{Executable}\" \"%1\"";
        var icon = $"\"{Executable}\",0";
        using (var url = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + UrlProgId))
        {
            url.SetValue(string.Empty, "URL:MailTo Protocol");
            url.SetValue("URL Protocol", string.Empty);
            url.CreateSubKey("DefaultIcon").SetValue(string.Empty, icon);
            url.CreateSubKey(@"shell\open\command").SetValue(string.Empty, command);
        }

        using (var eml = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + EmlProgId))
        {
            eml.SetValue(string.Empty, "E-Mail (Neruna)");
            eml.CreateSubKey("DefaultIcon").SetValue(string.Empty, icon);
            eml.CreateSubKey(@"shell\open\command").SetValue(string.Empty, command);
        }

        Registry.CurrentUser.CreateSubKey(@"Software\Classes\.eml\OpenWithProgids").SetValue(EmlProgId, Array.Empty<byte>(), RegistryValueKind.None);

        using (var client = Registry.CurrentUser.CreateSubKey(ClientKey))
        {
            client.SetValue(string.Empty, AppName);
            client.CreateSubKey(@"shell\open\command").SetValue(string.Empty, $"\"{Executable}\"");
            using var capabilities = client.CreateSubKey("Capabilities");
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "E-Mail, Kalender und Kontakte");
            capabilities.SetValue("ApplicationIcon", icon);
            capabilities.CreateSubKey("URLAssociations").SetValue("mailto", UrlProgId);
            capabilities.CreateSubKey("FileAssociations").SetValue(".eml", EmlProgId);
        }

        Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications").SetValue(AppName, ClientKey + @"\Capabilities");
        NotifyShell();
    }

    private static string? UserChoice(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue("ProgId") as string;
    }

    // Explorer re-reads the associations.
    private static void NotifyShell() => NativeMethods.SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    private static class NativeMethods
    {
        [DllImport("shell32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}

/// <summary>freedesktop: a .desktop file in ~/.local/share/applications and xdg-mime for both types.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxDefaultMailApp : DefaultMailApp
{
    private const string DesktopFile = "neruna.desktop";
    private static readonly string[] Types = ["x-scheme-handler/mailto", "message/rfc822"];

    private static string Applications => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
        "applications");

    public override DefaultMailStatus GetStatus() => new(
        Run("xdg-mime", "query", "default", Types[0]) == DesktopFile,
        Run("xdg-mime", "query", "default", Types[1]) == DesktopFile);

    public override bool MakeDefault()
    {
        WriteDesktopFile();
        foreach (var type in Types)
        {
            Run("xdg-mime", "default", DesktopFile, type);
        }

        return false;
    }

    public override void Refresh()
    {
        if (File.Exists(Path.Combine(Applications, DesktopFile)))
        {
            WriteDesktopFile();
        }
    }

    private static void WriteDesktopFile()
    {
        Directory.CreateDirectory(Applications);
        var exec = Executable.Contains(' ', StringComparison.Ordinal) ? $"\"{Executable}\"" : Executable;
        File.WriteAllText(Path.Combine(Applications, DesktopFile), $"""
            [Desktop Entry]
            Type=Application
            Name=Neruna
            Comment=E-Mail, Kalender und Kontakte
            Exec={exec} %u
            Icon={WriteIcon()}
            Terminal=false
            Categories=Office;Email;Calendar;ContactManagement;
            MimeType={string.Join(';', Types)};

            """);
        Run("update-desktop-database", Applications);
    }

    // The app icon from the resources next to the .desktop file's data (desktops read .ico via gdk-pixbuf/Qt).
    private static string WriteIcon()
    {
        var path = Path.Combine(Path.GetDirectoryName(Applications)!, "icons", "neruna.ico");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var source = Avalonia.Platform.AssetLoader.Open(new Uri("avares://neruna/Assets/app.ico"));
        using var target = File.Create(path);
        source.CopyTo(target);
        return path;
    }
}

/// <summary>
/// Launch Services: the bundle declares mailto and .eml in its Info.plist (see scripts/publish.sh); here Neruna asks
/// to be the default handler – macOS confirms that with the user.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacDefaultMailApp : DefaultMailApp
{
    private const string BundleId = "org.neruna.desktop";
    private const string EmailType = "com.apple.mail.email";
    private const uint AllRoles = 0xFFFFFFFF;

    public override DefaultMailStatus GetStatus() => new(
        string.Equals(Native.DefaultForScheme("mailto"), BundleId, StringComparison.OrdinalIgnoreCase),
        string.Equals(Native.DefaultForType(EmailType), BundleId, StringComparison.OrdinalIgnoreCase));

    public override bool MakeDefault()
    {
        Native.SetDefaultForScheme("mailto", BundleId);
        Native.SetDefaultForType(EmailType, BundleId, AllRoles);
        return false;
    }

    private static class Native
    {
        private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const uint Utf8 = 0x08000100;

        public static string? DefaultForScheme(string scheme) => WithString(scheme, s => Read(LSCopyDefaultHandlerForURLScheme(s)));

        public static string? DefaultForType(string type) => WithString(type, t => Read(LSCopyDefaultRoleHandlerForContentType(t, AllRoles)));

        public static void SetDefaultForScheme(string scheme, string bundle) =>
            WithString(scheme, s => WithString(bundle, b => LSSetDefaultHandlerForURLScheme(s, b)));

        public static void SetDefaultForType(string type, string bundle, uint roles) =>
            WithString(type, t => WithString(bundle, b => LSSetDefaultRoleHandlerForContentType(t, roles, b)));

        private static T WithString<T>(string value, Func<IntPtr, T> use)
        {
            var handle = CFStringCreateWithCString(IntPtr.Zero, value, Utf8);
            try
            {
                return use(handle);
            }
            finally
            {
                CFRelease(handle);
            }
        }

        private static string? Read(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var buffer = new byte[512];
                return CFStringGetCString(handle, buffer, buffer.Length, Utf8)
                    ? System.Text.Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is var end and >= 0 ? end : buffer.Length)
                    : null;
            }
            finally
            {
                CFRelease(handle);
            }
        }

        [DllImport(CoreFoundation, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CFStringGetCString(IntPtr handle, byte[] buffer, long size, uint encoding);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr handle);

        [DllImport(CoreServices)]
        private static extern IntPtr LSCopyDefaultHandlerForURLScheme(IntPtr scheme);

        [DllImport(CoreServices)]
        private static extern IntPtr LSCopyDefaultRoleHandlerForContentType(IntPtr type, uint role);

        [DllImport(CoreServices)]
        private static extern int LSSetDefaultHandlerForURLScheme(IntPtr scheme, IntPtr bundle);

        [DllImport(CoreServices)]
        private static extern int LSSetDefaultRoleHandlerForContentType(IntPtr type, uint role, IntPtr bundle);
    }
}
