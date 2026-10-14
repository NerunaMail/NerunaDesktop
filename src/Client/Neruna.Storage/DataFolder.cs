namespace Neruna.Storage;

/// <summary>
/// Where Neruna keeps its data. On Windows not in <c>%LocalAppData%\Neruna</c>: the installer (Velopack, package id
/// "Neruna") installs the program there and clears that folder when Neruna is installed again or uninstalled – the data
/// of a portable copy used before, or of an installation being reinstalled, was lost with it. The data lives in
/// <c>%LocalAppData%\NerunaData</c> instead and is moved there once from the old place.
/// </summary>
public static class DataFolder
{
    // What Neruna writes into its data folder (everything else in the old folder belongs to the installer).
    private static readonly string[] Entries =
    [
        "neruna.db", "neruna.db-wal", "neruna.db-shm", "messages", "logs", "crashes", "credentials.json", "credentials.key", "language", "demo",
    ];

    public static string Default()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        var old = Path.Combine(local, "Neruna");
        if (!OperatingSystem.IsWindows())
        {
            return old;
        }

        var data = Path.Combine(local, "NerunaData");
        return MoveOnce(old, data) ? data : old;
    }

    /// <summary>
    /// Moves the data from <paramref name="from"/> when <paramref name="to"/> has none yet. False when it could not be
    /// moved (another Neruna still running): this start works in the old place, the next one tries again.
    /// </summary>
    public static bool MoveOnce(string from, string to)
    {
        if (File.Exists(Path.Combine(to, "neruna.db")) || !File.Exists(Path.Combine(from, "neruna.db")))
        {
            Directory.CreateDirectory(to);
            return true;
        }

        Directory.CreateDirectory(to);
        try
        {
            foreach (var name in Entries)
            {
                var source = Path.Combine(from, name);
                var target = Path.Combine(to, name);
                if (Directory.Exists(source) && !Directory.Exists(target))
                {
                    Directory.Move(source, target);
                }
                else if (File.Exists(source) && !File.Exists(target))
                {
                    File.Move(source, target);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Partly moved: put the database back, so this start finds everything in one place.
            foreach (var name in Entries.Where(n => n.StartsWith("neruna.db", StringComparison.Ordinal)))
            {
                var moved = Path.Combine(to, name);
                if (File.Exists(moved) && !File.Exists(Path.Combine(from, name)))
                {
                    File.Move(moved, Path.Combine(from, name));
                }
            }

            return false;
        }
    }
}
