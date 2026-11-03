using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Neruna.Desktop.Infrastructure;

/// <summary>Whether other programs reach Neruna through Simple MAPI.</summary>
internal enum MapiState
{
    /// <summary>Not on this system (Linux, macOS) or the DLLs are missing (a development build).</summary>
    Unavailable,

    /// <summary>Not set up.</summary>
    Off,

    /// <summary>The DLLs are registered for the computer, but this user's programs still use another mail program.</summary>
    OtherDefault,

    /// <summary>Programs of this user hand their mails to Neruna.</summary>
    On,
}

/// <summary>
/// Simple MAPI on Windows ("Per E-Mail senden" in accounting software, PDF tools …): Windows looks up the DLL of the
/// default mail program only for the whole computer, in HKLM\Software\Clients\Mail\Neruna\DLLPath – a key 32-bit and
/// 64-bit programs share, so one path has to serve both. It therefore names %SystemRoot%\System32\NerunaMapi.dll:
/// the 64-bit DLL lies there, the 32-bit one under the same name in SysWOW64, where Windows redirects 32-bit programs
/// asking for System32. Setting it up asks for administrator rights once (copying, registering); every user of a
/// terminal server then reaches the DLLs, and each one's own neruna.exe is found through his registration. Which mail
/// program a user's programs use is his own choice (HKCU\Software\Clients\Mail).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MapiSetup
{
    private const string ClientsKey = @"Software\Clients\Mail";
    private const string NerunaKey = ClientsKey + @"\Neruna";
    private const string Dll64 = "NerunaMapi64.dll";
    private const string Dll32 = "NerunaMapi32.dll";
    private const string Installed = "NerunaMapi.dll";

    // Many programs ask here first ("MAPI services are not available"); missing on servers without Outlook. Unlike
    // Clients, this key is separate for 32-bit programs (WOW6432Node).
    private const string MessagingKey = @"Software\Microsoft\Windows Messaging Subsystem";

    /// <summary>The path in the registry; 32-bit programs land in SysWOW64.</summary>
    private static string Target64 => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), Installed);

    private static string Target32 => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), Installed);

    // 0.25.0/0.25.1 copied them here (one path per bitness, which the shared key cannot hold): removed when setting up.
    private static string OldFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Neruna", "MAPI");

    private static string Shipped(string dll) => Path.Combine(AppContext.BaseDirectory, dll);

    public static MapiState GetState()
    {
        if (!File.Exists(Shipped(Dll64)) || !File.Exists(Shipped(Dll32)))
        {
            return MapiState.Unavailable;
        }

        if (!IsRegistered())
        {
            return MapiState.Off;
        }

        using var user = Registry.CurrentUser.OpenSubKey(ClientsKey);
        return user?.GetValue(string.Empty) as string == "Neruna" ? MapiState.On : MapiState.OtherDefault;
    }

    /// <summary>
    /// Copies and registers the DLLs (one confirmation by Windows), then makes Neruna this user's MAPI program.
    /// Whether it worked is read from the registry and the files afterwards (not from the exit code of the elevated cmd).
    /// </summary>
    public static async Task<MapiSetupResult> SetUpAsync()
    {
        if (!IsRegistered() || IsOutdated())
        {
            var ran = await RunElevatedAsync(
            [
                $"copy /y \"{Shipped(Dll64)}\" \"{Target64}\"",
                $"copy /y \"{Shipped(Dll32)}\" \"{Target32}\"",
                $"reg add \"HKLM\\{NerunaKey}\" /ve /d Neruna /t REG_SZ /f /reg:64",
                $"reg add \"HKLM\\{NerunaKey}\" /v DLLPath /d \"{Target64}\" /t REG_SZ /f /reg:64",
                $"reg add \"HKLM\\{MessagingKey}\" /v MAPI /d 1 /t REG_SZ /f /reg:64",
                $"reg add \"HKLM\\{MessagingKey}\" /v MAPI /d 1 /t REG_SZ /f /reg:32",
                // In brackets: "if" would otherwise take everything after it as its own command.
                $"(if exist \"{OldFolder}\" rmdir /s /q \"{OldFolder}\")",
            ]);
            if (ran.Declined)
            {
                return ran;
            }

            if (!IsRegistered())
            {
                return ran with { Ok = false };
            }
        }

        // This user's programs: Neruna (Windows reads the user's choice before the computer's).
        Registry.CurrentUser.CreateSubKey(ClientsKey).SetValue(string.Empty, "Neruna");
        DefaultMailApp.Current?.Refresh();
        return new MapiSetupResult(true, false, null);
    }

    /// <summary>This user's programs use the computer's mail program again; with administrator rights also unregistered.</summary>
    public static async Task<MapiSetupResult> RemoveAsync()
    {
        using (var user = Registry.CurrentUser.OpenSubKey(ClientsKey, writable: true))
        {
            if (user?.GetValue(string.Empty) as string == "Neruna")
            {
                user.DeleteValue(string.Empty, throwOnMissingValue: false);
            }
        }

        var ran = await RunElevatedAsync(
        [
            $"reg delete \"HKLM\\{NerunaKey}\" /v DLLPath /f /reg:64",
            $"del /f /q \"{Target64}\"",
            $"del /f /q \"{Target32}\"",
            $"(if exist \"{OldFolder}\" rmdir /s /q \"{OldFolder}\")",
        ], stopOnError: false);
        return ran.Declined ? ran : ran with { Ok = !IsRegistered() };
    }

    private static bool IsRegistered()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(NerunaKey);
        return key?.GetValue("DLLPath") is string path
               && string.Equals(path, Target64, StringComparison.OrdinalIgnoreCase)
               && File.Exists(Target64)
               && File.Exists(Target32)
               && MapiAvailable(RegistryView.Registry64)
               && MapiAvailable(RegistryView.Registry32);
    }

    private static bool MapiAvailable(RegistryView view)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = machine.OpenSubKey(MessagingKey);
        return key?.GetValue("MAPI") as string == "1";
    }

    // A newer Neruna ships a changed DLL: the installed copies are renewed with the next "Einrichten".
    private static bool IsOutdated() =>
        new[] { (Dll64, Target64), (Dll32, Target32) }.Any(pair => !File.Exists(pair.Item2)
            || !File.ReadAllBytes(Shipped(pair.Item1)).AsSpan().SequenceEqual(File.ReadAllBytes(pair.Item2)));

    // What the elevated cmd wrote (also when it ran as another user, an administrator signing in): ProgramData is
    // writable for administrators and readable for everyone.
    private static string LogFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Neruna", "mapi-setup.log");

    // One prompt by Windows for all commands (cmd as administrator, no window); its output goes to LogFile.
    private static async Task<MapiSetupResult> RunElevatedAsync(IEnumerable<string> commands, bool stopOnError = true)
    {
        var log = LogFile;
        var logFolder = Path.GetDirectoryName(log)!;
        var script = $"(if not exist \"{logFolder}\" mkdir \"{logFolder}\") & ({string.Join(stopOnError ? " && " : " & ", commands)}) > \"{log}\" 2>&1";
        var start = new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return new MapiSetupResult(false, false, null);
            }

            await process.WaitForExitAsync();
            var output = ReadLog(log);
            CrashHandler.Logger?.LogInformation("MAPI setup: exit code {Code}, output: {Output}", process.ExitCode, output);
            return new MapiSetupResult(process.ExitCode == 0, false, output);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new MapiSetupResult(false, true, null); // ERROR_CANCELLED: the user said no
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new MapiSetupResult(false, false, ex.Message);
        }
    }

    private static string? ReadLog(string log)
    {
        try
        {
            var text = File.ReadAllText(log).Trim();
            return text.Length == 0 ? null : text.Length > 600 ? text[^600..] : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The outcome of setting up or removing; <see cref="Detail"/> is what Windows answered (for the hint).</summary>
internal sealed record MapiSetupResult(bool Ok, bool Declined, string? Detail);
