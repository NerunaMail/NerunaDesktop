using System.Text.Json;
using MimeKit;

namespace Neruna.Core.Mail;

/// <summary>
/// A mail another program hands over through Simple MAPI ("Per E-Mail senden" in accounting software, PDF tools …):
/// Neruna's MAPI DLL (native/mapi) writes recipients, subject, text and copies of the attachments into a folder of its
/// own and starts Neruna with the ".nerunamapi" file. Neruna opens it as a new mail – nothing is sent without the user.
/// </summary>
public static class MapiRequest
{
    public const string Extension = ".nerunamapi";

    /// <summary>The temporary folder the DLL writes into (%TEMP%\Neruna-MAPI\…).</summary>
    public const string FolderName = "Neruna-MAPI";

    /// <summary>A request of the DLL: the file in a folder below %TEMP%\Neruna-MAPI (nothing else is taken).</summary>
    public static bool IsRequest(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
        && Path.GetFullPath(path).Contains(Path.DirectorySeparatorChar + FolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);

    /// <summary>The new mail; the attachments are read into memory, the DLL's folder is deleted afterwards.</summary>
    public static async Task<ComposeDraft> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        try
        {
            await using var stream = File.OpenRead(path);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = json.RootElement;

            var to = new List<string>();
            var cc = new List<string>();
            var bcc = new List<string>();
            foreach (var recipient in Items(root, "recipients"))
            {
                if (Address(Text(recipient, "name"), Text(recipient, "address")) is not { } address)
                {
                    continue;
                }

                (Text(recipient, "kind") switch
                {
                    "cc" => cc,
                    "bcc" => bcc,
                    _ => to,
                }).Add(address);
            }

            var attachments = new List<MimeEntity>();
            var builder = new BodyBuilder();
            foreach (var attachment in Items(root, "attachments"))
            {
                var file = Text(attachment, "path");
                // Only the DLL's own copies, never a path elsewhere a forged request might name.
                if (string.IsNullOrEmpty(file) || !IsInside(file, folder))
                {
                    continue;
                }

                var name = Text(attachment, "name") is { Length: > 0 } shown ? shown : Path.GetFileName(file);
                attachments.Add(await builder.Attachments.AddAsync(name, new MemoryStream(await File.ReadAllBytesAsync(file, cancellationToken)), cancellationToken));
            }

            return ComposeDraft.Empty with
            {
                To = string.Join(", ", to),
                Cc = string.Join(", ", cc),
                Subject = Text(root, "subject") ?? string.Empty,
                Body = (Text(root, "body") ?? string.Empty).ReplaceLineEndings("\n"),
                Attachments = attachments,
                Bcc = string.Join(", ", bcc),
            };
        }
        finally
        {
            Delete(folder);
        }
    }

    /// <summary>Folders older than a day (Neruna was not running, or the mail was never opened) are removed.</summary>
    public static void CleanUp(string tempFolder, TimeSpan age)
    {
        var root = Path.Combine(tempFolder, FolderName);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > age)
            {
                Delete(folder);
            }
        }
    }

    // "Anna Muster" + "anna@example.com" → "Anna Muster <anna@example.com>"; only a name with an @ is the address.
    internal static string? Address(string? name, string? address)
    {
        name = name?.Trim();
        address = address?.Trim();
        if (address is { Length: > 0 } && address.StartsWith("SMTP:", StringComparison.OrdinalIgnoreCase))
        {
            address = address[5..];
        }

        if (string.IsNullOrEmpty(address))
        {
            return name is { Length: > 0 } && name.Contains('@', StringComparison.Ordinal) ? name : null;
        }

        return string.IsNullOrEmpty(name) || string.Equals(name, address, StringComparison.OrdinalIgnoreCase)
            ? address
            : new MailboxAddress(name, address).ToString();
    }

    private static bool IsInside(string file, string folder)
    {
        var full = Path.GetFullPath(file);
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
               && File.Exists(full);
    }

    private static JsonElement[] Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? [.. items.EnumerateArray()] : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void Delete(string folder)
    {
        // Only folders below Neruna-MAPI: never a folder a forged path points to.
        if (!Path.GetFullPath(folder).Contains(Path.DirectorySeparatorChar + FolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next start (CleanUp).
        }
    }
}
