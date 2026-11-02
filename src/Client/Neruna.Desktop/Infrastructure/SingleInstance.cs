using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// One Neruna per data directory: a second start (a mailto: link or .eml file opened from the system, or just a
/// double-click) hands its arguments to the running one over a local pipe and ends – two processes on the same
/// database would get in each other's way.
/// </summary>
internal static class SingleInstance
{
    private static string PipeName(AppOptions options) => PipeName(options.DataDirectory);

    private static string PipeName(string dataDirectory) =>
        "neruna-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory))))[..16];

    /// <summary>Whether a Neruna has this data folder open (a profile must not be deleted under it).</summary>
    public static bool IsRunning(string dataDirectory)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(dataDirectory), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(200);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>True when another Neruna took the arguments (this process should end).</summary>
    public static bool TryHandOver(AppOptions options, string[] args) => TrySend(options.DataDirectory, args);

    /// <summary>Hands <paramref name="args"/> to the Neruna that has this data folder open; false when none has.</summary>
    public static bool TrySend(string dataDirectory, string[] args)
    {
        try
        {
            // CurrentUserOnly: hand links and files only to a Neruna of this user, never to a pipe another user opened.
            using var pipe = new NamedPipeClientStream(".", PipeName(dataDirectory), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(300);
            // Windows lets only the program the user just started (this one) bring a window to the front: the running
            // Neruna may take over, else its window and the opened mail stay behind Explorer, only blinking in the taskbar.
            if (OperatingSystem.IsWindows())
            {
                NativeMethods.AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            }

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(args));
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false; // nobody listening: this is the first one
        }
    }

    /// <summary>
    /// A mailto: link or .eml file without a chosen profile: to the open profile if the start profile is not open (a
    /// file opened from Explorer goes to the Neruna that is already on screen, not into a second window).
    /// </summary>
    public static bool TryHandOverToOpenProfile(AppOptions options, string[] args)
    {
        if (options.Root is not { } root || !args.Any(SystemOpen.IsOpenable) || Neruna.Storage.ProfileList.NameIn(args) is not null)
        {
            return false;
        }

        var profiles = Neruna.Storage.ProfileList.Load(root);
        return profiles.All.Select(profiles.DirectoryOf)
            .Where(directory => !string.Equals(Path.GetFullPath(directory), Path.GetFullPath(options.DataDirectory), StringComparison.OrdinalIgnoreCase))
            .Any(directory => TrySend(directory, args));
    }

    /// <summary>Receives the arguments of later starts; <paramref name="received"/> runs on a background thread.</summary>
    public static void Listen(AppOptions options, Action<string[]> received, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(PipeName(options), PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    if (await reader.ReadLineAsync(cancellationToken) is { } line && JsonSerializer.Deserialize<string[]>(line) is { } args)
                    {
                        received(args);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    await Task.Delay(200, CancellationToken.None);
                }
            }
        }, CancellationToken.None);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AllowSetForegroundWindow(int processId);
    }
}
