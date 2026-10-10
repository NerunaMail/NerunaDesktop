using System.IO.Pipes;
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
    private static string PipeName(AppOptions options) =>
        "neruna-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(options.DataDirectory))))[..16];

    /// <summary>True when another Neruna took the arguments (this process should end).</summary>
    public static bool TryHandOver(AppOptions options, string[] args)
    {
        try
        {
            // CurrentUserOnly: hand links and files only to a Neruna of this user, never to a pipe another user opened.
            using var pipe = new NamedPipeClientStream(".", PipeName(options), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(300);
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
}
