using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Neruna.Core.Security;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Infrastructure;

/// <summary>File pickers and "open with default app", so view models stay free of windows and platform code.</summary>
internal interface IFileService
{
    /// <returns>Local paths of the chosen files (empty when cancelled).</returns>
    Task<IReadOnlyList<string>> PickFilesAsync(string title);

    /// <returns>The local path of one chosen file of the given kind (e.g. "*.pst"), or null when cancelled.</returns>
    Task<string?> PickFileAsync(string title, string kind, IReadOnlyList<string> patterns);

    /// <returns>A writable stream for the chosen target, or null when cancelled.</returns>
    /// <param name="folder">Where the dialog starts (a quick action's folder); the system's choice when null or missing.</param>
    Task<Stream?> SaveFileAsync(string title, string suggestedName, string? folder = null);

    /// <summary>A save dialog with a choice of types (e.g. EML or PDF), the preferred one first.</summary>
    /// <returns>The stream, the chosen type's extension (".pdf") and the local path; null when cancelled.</returns>
    Task<(Stream Stream, string Extension, string? Path)?> SaveFileAsAsync(string title, string baseName, IReadOnlyList<(string Name, string Extension)> types);

    /// <returns>The local path of the chosen folder, or null when cancelled.</returns>
    Task<string?> PickFolderAsync(string title);

    /// <summary>
    /// Writes the content to a private temp file, marks it as coming from the internet and opens it with the system's
    /// default application – programs and scripts only after a confirmation. False when that was declined.
    /// </summary>
    Task<bool> OpenAsync(string fileName, Func<Stream, Task> writeContent);

    Task OpenFolderAsync(string path);

    /// <summary>Sends the file to the default printer (Windows: its program's "print"; macOS and Linux: lp).</summary>
    Task PrintAsync(string fileName, byte[] content);
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

    public async Task<string?> PickFileAsync(string title, string kind, IReadOnlyList<string> patterns)
    {
        if (Window is not { } window)
        {
            return null;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(kind) { Patterns = [.. patterns] }, FilePickerFileTypes.All],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<Stream?> SaveFileAsync(string title, string suggestedName, string? folder = null)
    {
        if (Window is not { } window)
        {
            return null;
        }

        var start = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? await window.StorageProvider.TryGetFolderFromPathAsync(folder) : null;
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName, SuggestedStartLocation = start });
        return file is null ? null : await file.OpenWriteAsync();
    }

    public async Task<(Stream Stream, string Extension, string? Path)?> SaveFileAsAsync(string title, string baseName, IReadOnlyList<(string Name, string Extension)> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (Window is not { } window || types.Count == 0)
        {
            return null;
        }

        var choices = types.Select(t => new FilePickerFileType(t.Name) { Patterns = ["*" + t.Extension] }).ToList();
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = baseName + types[0].Extension,
            DefaultExtension = types[0].Extension.TrimStart('.'),
            FileTypeChoices = choices,
            SuggestedFileType = choices[0],
        });
        if (file is null)
        {
            return null;
        }

        // The type follows the name the person ended up with; without a known ending the one offered first.
        var extension = types.Select(t => t.Extension).FirstOrDefault(e => file.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ?? types[0].Extension;
        return (await file.OpenWriteAsync(), extension, file.TryGetLocalPath());
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Window is not { } window)
        {
            return null;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<bool> OpenAsync(string fileName, Func<Stream, Task> writeContent)
    {
        ArgumentNullException.ThrowIfNull(writeContent);
        if (Window is not { } window)
        {
            return false;
        }

        var name = SanitizeFileName(fileName);
        if (OpenedAttachments.IsRisky(name) && !await ConfirmRiskyAsync(name))
        {
            return false;
        }

        // A fresh private folder per file avoids clashes and keeps the original name for the opening application.
        var path = Path.Combine(OpenedAttachments.CreateFolder(), name);
        await using (var file = File.Create(path))
        {
            await writeContent(file);
        }

        OpenedAttachments.MarkAsFromInternet(path);
        await window.Launcher.LaunchFileInfoAsync(new FileInfo(path));
        return true;
    }

    // Asked over the window in front (a message window, or the main window).
    private static Task<bool> ConfirmRiskyAsync(string name) =>
        Views.ChoiceDialog.ShowInFrontAsync(T("Programm im Anhang"),
            F("«{0}» ist ein Programm, ein Skript oder eine Verknüpfung. Damit übernehmen Angreifer Computer – öffnen Sie es nur, wenn Sie den Absender kennen und genau diesen Anhang erwartet haben.", name),
            (T("Trotzdem öffnen"), true, false),
            (T("Abbrechen"), false, true));

    public async Task OpenFolderAsync(string path)
    {
        if (Window is { } window)
        {
            await window.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
    }

    public async Task PrintAsync(string fileName, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = Path.Combine(OpenedAttachments.CreateFolder(), SanitizeFileName(fileName));
        await File.WriteAllBytesAsync(path, content);
        OpenedAttachments.MarkAsFromInternet(path);
        var start = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, Verb = "print" }
            : new System.Diagnostics.ProcessStartInfo("lp", [path]) { UseShellExecute = false };
        using var process = System.Diagnostics.Process.Start(start);
    }

    internal static string SanitizeFileName(string name)
    {
        // Characters no file system takes (also those Windows forbids, so a name works on every system) and
        // invisible ones that disguise the type; ".." and the like are no names.
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var clean = new string(OpenedAttachments.CleanName(name).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return clean.Trim('.').Length == 0 ? "anhang" : clean;
    }
}
