using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Neruna.Desktop.Editor;

/// <summary>Files copied in the file manager (Ctrl+C there), as local paths – for pasting them as attachments.</summary>
internal static class ClipboardFiles
{
    public static async Task<IReadOnlyList<string>> PathsAsync(IClipboard? clipboard)
    {
        if (clipboard is null)
        {
            return [];
        }

        try
        {
            return await clipboard.TryGetFilesAsync() is { Length: > 0 } items
                ? [.. items.Select(i => i.TryGetLocalPath()).OfType<string>().Where(File.Exists)]
                : [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // The clipboard is busy (another program holds it) or holds something unreadable: nothing to attach.
            return [];
        }
    }
}
