using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Neruna.Desktop.Infrastructure;

/// <summary>File pickers and "open with default app", so view models stay free of windows and platform code.</summary>
internal interface IFileService
{
    /// <returns>Local paths of the chosen files (empty when cancelled).</returns>
    Task<IReadOnlyList<string>> PickFilesAsync(string title);

    /// <returns>A writable stream for the chosen target, or null when cancelled.</returns>
    Task<Stream?> SaveFileAsync(string title, string suggestedName);

    /// <summary>Writes the content to a temp file and opens it with the system's default application.</summary>
    Task OpenAsync(string fileName, Func<Stream, Task> writeContent);

    Task OpenFolderAsync(string path);
}

internal sealed class FileService : IFileService
{
    private static TopLevel? Window =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        if (Window is not { } window)
        {
            return [];
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<Stream?> SaveFileAsync(string title, string suggestedName)
    {
        if (Window is not { } window)
        {
            return null;
        }

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName });
        return file is null ? null : await file.OpenWriteAsync();
    }

    public async Task OpenAsync(string fileName, Func<Stream, Task> writeContent)
    {
        ArgumentNullException.ThrowIfNull(writeContent);

        // A fresh folder per file avoids clashes and keeps the original name for the opening application.
        var directory = Path.Combine(Path.GetTempPath(), "Neruna", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SanitizeFileName(fileName));
        await using (var file = File.Create(path))
        {
            await writeContent(file);
        }

        if (Window is { } window)
        {
            await window.Launcher.LaunchFileInfoAsync(new FileInfo(path));
        }
    }

    public async Task OpenFolderAsync(string path)
    {
        if (Window is { } window)
        {
            await window.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
    }

    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "anhang" : clean;
    }
}
