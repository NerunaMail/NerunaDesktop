using Avalonia.Media;

namespace Neruna.Desktop.Editor;

/// <summary>One entry of the font dropdown; the name is rendered in the font itself.</summary>
internal sealed record FontChoice(string Name, FontFamily Family, bool IsSeparator = false)
{
    public bool IsFont => !IsSeparator;
}

/// <summary>The installed fonts, with the typical mail fonts first.</summary>
internal static class FontCatalog
{
    private static readonly string[] Preferred =
    [
        "Aptos", "Calibri", "Arial", "Segoe UI", "Helvetica", "Verdana", "Tahoma", "Georgia", "Times New Roman", "Cambria", "Courier New", "Consolas",
    ];

    private static readonly Lazy<IReadOnlyList<FontChoice>> All = new(Load);

    public static IReadOnlyList<FontChoice> Fonts => All.Value;

    public static IReadOnlyList<double> Sizes { get; } = [8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72];

    /// <summary>The first preferred font that is installed, as default for new mails.</summary>
    public static string DefaultFont => All.Value.FirstOrDefault(f => f.IsFont)?.Name ?? "Arial";

    public static FontChoice? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : All.Value.FirstOrDefault(f => f.IsFont && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static List<FontChoice> Load()
    {
        var installed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var family in FontManager.Current.SystemFonts)
            {
                installed.Add(family.Name);
            }
        }
        catch (InvalidOperationException)
        {
            // No font manager (e.g. during design-time); offer the preferred list only.
        }

        if (installed.Count == 0)
        {
            installed.UnionWith(Preferred);
        }

        var preferred = Preferred.Where(installed.Contains).Select(name => new FontChoice(name, new FontFamily(name))).ToList();
        var others = installed.Where(name => !Preferred.Contains(name, StringComparer.OrdinalIgnoreCase)).Select(name => new FontChoice(name, new FontFamily(name)));

        var result = new List<FontChoice>(preferred);
        if (preferred.Count > 0)
        {
            result.Add(new FontChoice(string.Empty, FontFamily.Default, IsSeparator: true));
        }

        result.AddRange(others);
        return result;
    }
}
