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

    /// <returns>A writable stream for the chosen target, or null when cancelled.</returns>
    Task<Stream?> SaveFileAsync(string title, string suggestedName);

    /// <summary>
    /// Writes the content to a private temp file, marks it as coming from the internet and opens it with the system's
    /// default application – programs and scripts only after a confirmation.
    /// </summary>
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
        if (Window is not { } window)
        {
            return;
        }

        var name = SanitizeFileName(fileName);
        if (OpenedAttachments.IsRisky(name) && !await ConfirmRiskyAsync(name))
        {
            return;
        }

        // A fresh private folder per file avoids clashes and keeps the original name for the opening application.
        var path = Path.Combine(OpenedAttachments.CreateFolder(), name);
        await using (var file = File.Create(path))
        {
            await writeContent(file);
        }

        OpenedAttachments.MarkAsFromInternet(path);
        await window.Launcher.LaunchFileInfoAsync(new FileInfo(path));
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

    internal static string SanitizeFileName(string name)
    {
        // Characters no file system takes (also those Windows forbids, so a name works on every system) and
        // invisible ones that disguise the type; ".." and the like are no names.
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var clean = new string(OpenedAttachments.CleanName(name).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return clean.Trim('.').Length == 0 ? "anhang" : clean;
    }
}
