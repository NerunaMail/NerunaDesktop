using System.Globalization;
using System.Text.Json;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Neruna.Desktop.Snapshot;

/// <summary>
/// The screenshots over the versions: <c>versions.json</c> in the output folder says which version and day each
/// picture shows. When a picture of an older version is replaced by a different one, the old one moves to
/// <c>archive/&lt;day&gt;_v&lt;version&gt;/</c> first – no extra screenshots, just the ones replaced. Within the same
/// version pictures are simply overwritten (the demo data's times change with every run).
/// </summary>
internal static class ScreenshotArchive
{
    // Less than this share of pixels changed (times, dates of the demo data): not worth an archived copy.
    private const double MinChange = 0.005;
    private const string IndexFile = "versions.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Version { get; } = typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>Saves <paramref name="frame"/> as <paramref name="name"/>, archiving the picture it replaces if due.</summary>
    public static void Save(Bitmap frame, string output, string name)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var path = Path.Combine(output, name);
        var index = Load(output);
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (File.Exists(path))
        {
            var old = index.TryGetValue(name, out var entry) ? entry : new Shot(Version, File.GetLastWriteTime(path).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (old.Version != Version)
            {
                var replaced = File.ReadAllBytes(path);
                using var fresh = new MemoryStream();
                frame.Save(fresh, PngBitmapEncoderOptions.Default);
                if (Changed(replaced, fresh.ToArray()))
                {
                    // Dark screenshots (docs/screenshots/dark) go into the same archive, in their own subfolder.
                    var dark = Path.GetFileName(Path.TrimEndingDirectorySeparator(output)) == "dark";
                    var archiveRoot = dark ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(output))! : output;
                    var archived = Path.Combine(archiveRoot, "archive", $"{old.Date}_v{old.Version}", dark ? Path.Combine("dark", name) : name);
                    Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
                    File.WriteAllBytes(archived, replaced);
                    Console.WriteLine($"archived {archived}");
                }
            }
        }

        frame.Save(path, PngBitmapEncoderOptions.Default);
        index[name] = new Shot(Version, today);
        File.WriteAllText(Path.Combine(output, IndexFile), JsonSerializer.Serialize(new SortedDictionary<string, Shot>(index, StringComparer.Ordinal), Json));
    }

    private static Dictionary<string, Shot> Load(string output)
    {
        var file = Path.Combine(output, IndexFile);
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, Shot>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // Another size, or more than a few pixels differ.
    private static bool Changed(byte[] before, byte[] after)
    {
        using var a = SKBitmap.Decode(before);
        using var b = SKBitmap.Decode(after);
        if (a is null || b is null || a.Width != b.Width || a.Height != b.Height)
        {
            return true;
        }

        var differing = 0L;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                {
                    differing++;
                }
            }
        }

        return differing > MinChange * a.Width * a.Height;
    }

    internal sealed record Shot(string Version, string Date);
}
