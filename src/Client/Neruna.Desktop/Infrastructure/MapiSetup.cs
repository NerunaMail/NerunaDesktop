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
/// default mail program only for the whole computer (HKLM\Software\Clients\Mail\Neruna\DLLPath, for 32-bit programs
/// under WOW6432Node). Setting it up therefore asks for administrator rights once: the two DLLs are copied to
/// %ProgramFiles%\Neruna\MAPI (so every user of a terminal server reaches them; each one's own neruna.exe is found
/// through his registration) and registered. Which mail program a user's programs use is his own choice (HKCU).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MapiSetup
{
    private const string ClientsKey = @"Software\Clients\Mail";
    private const string NerunaKey = ClientsKey + @"\Neruna";
    private const string Dll64 = "NerunaMapi64.dll";
    private const string Dll32 = "NerunaMapi32.dll";

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Neruna", "MAPI");

    private static string Shipped(string dll) => Path.Combine(AppContext.BaseDirectory, dll);

    public static MapiState GetState()
    {
        if (!File.Exists(Shipped(Dll64)) || !File.Exists(Shipped(Dll32)))
        {
            return MapiState.Unavailable;
        }

        if (!IsRegistered(RegistryView.Registry64, Dll64) || !IsRegistered(RegistryView.Registry32, Dll32))
        {
            return MapiState.Off;
        }

        using var user = Registry.CurrentUser.OpenSubKey(ClientsKey);
        return user?.GetValue(string.Empty) as string == "Neruna" ? MapiState.On : MapiState.OtherDefault;
    }

    /// <summary>
    /// Copies and registers the DLLs (one confirmation by Windows), then makes Neruna this user's MAPI program.
    /// Whether it worked is read from the registry afterwards (not from the exit code of the elevated cmd).
    /// </summary>
    public static async Task<MapiSetupResult> SetUpAsync()
    {
        if (!IsRegistered(RegistryView.Registry64, Dll64) || !IsRegistered(RegistryView.Registry32, Dll32) || IsOutdated())
        {
            var ran = await RunElevatedAsync(
            [
                // In brackets: "if" would otherwise take everything after it as its own command.
                $"(if not exist \"{Folder}\" mkdir \"{Folder}\")",
                $"copy /y \"{Shipped(Dll64)}\" \"{Path.Combine(Folder, Dll64)}\"",
                $"copy /y \"{Shipped(Dll32)}\" \"{Path.Combine(Folder, Dll32)}\"",
                Reg(64, "/ve /d Neruna"),
                Reg(64, $"/v DLLPath /d \"{Path.Combine(Folder, Dll64)}\""),
                Reg(32, "/ve /d Neruna"),
                Reg(32, $"/v DLLPath /d \"{Path.Combine(Folder, Dll32)}\""),
            ]);
            if (ran.Declined)
            {
                return ran;
            }

            if (!IsRegistered(RegistryView.Registry64, Dll64) || !IsRegistered(RegistryView.Registry32, Dll32))
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
            $"reg delete \"HKLM\\{NerunaKey}\" /v DLLPath /f /reg:32",
            $"rmdir /s /q \"{Folder}\"",
        ], stopOnError: false);
        return ran.Declined ? ran : ran with { Ok = !IsRegistered(RegistryView.Registry64, Dll64) && !IsRegistered(RegistryView.Registry32, Dll32) };
    }

    private static string Reg(int bits, string value) => $"reg add \"HKLM\\{NerunaKey}\" {value} /t REG_SZ /f /reg:{bits}";

    private static bool IsRegistered(RegistryView view, string dll)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = machine.OpenSubKey(NerunaKey);
        return key?.GetValue("DLLPath") is string path
               && string.Equals(path, Path.Combine(Folder, dll), StringComparison.OrdinalIgnoreCase)
               && File.Exists(path);
    }

    // A newer Neruna ships a changed DLL: the copy in Program Files is renewed with the next "Einrichten".
    private static bool IsOutdated() =>
        new[] { Dll64, Dll32 }.Any(dll => !File.Exists(Path.Combine(Folder, dll))
                                          || !File.ReadAllBytes(Shipped(dll)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder, dll))));

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
