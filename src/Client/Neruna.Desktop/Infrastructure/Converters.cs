using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Neruna.Contracts.Discovery;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop;

internal static class Converters
{
    /// <summary>A workspace colour (#RRGGBB) as a brush.</summary>
    public static FuncValueConverter<string?, Avalonia.Media.IBrush> WorkspaceBrush { get; } = new(ViewModels.WorkspaceColors.Brush);

    public static FuncValueConverter<SocketSecurity, string> SecurityLabel { get; } = new(security => security switch
    {
        SocketSecurity.SslOnConnect => "SSL/TLS",
        SocketSecurity.StartTls => "STARTTLS",
        _ => T("Keine (unverschlüsselt)"),
    });

    /// <summary>"#RRGGBB" → brush, for color swatches.</summary>
    public static FuncValueConverter<string?, IBrush> ColorBrush { get; } = new(color =>
        Color.TryParse(color, out var parsed) ? new SolidColorBrush(parsed) : Brushes.Transparent);

    /// <summary>Greyed out while another list is shown alone ("Nur diesen … anzeigen").</summary>
    /// <summary>Settings: the page whose number is the parameter is shown.</summary>
    public static FuncValueConverter<int, object?, bool> IsTab { get; } = new((tab, parameter) =>
        parameter is not null && int.TryParse(parameter.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var wanted) && tab == wanted);

    public static FuncValueConverter<bool, double> DimmedOpacity { get; } = new(dimmed => dimmed ? 0.45 : 1);

    public static FuncValueConverter<bool, FontWeight> BoldIf { get; } = new(bold => bold ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>A ring around the chosen one (e.g. the account color in its picker).</summary>
    public static FuncValueConverter<bool, Avalonia.Thickness> ThicknessIf { get; } = new(chosen => chosen ? new Avalonia.Thickness(3) : new Avalonia.Thickness(0));

    /// <summary>Shown or only taking its place (a table column keeps its icons aligned).</summary>
    public static FuncValueConverter<bool, double> OpaqueIf { get; } = new(shown => shown ? 1 : 0);

    /// <summary>The editor joins the formatting toolbar above it; alone it gets all corners rounded.</summary>
    public static FuncValueConverter<bool, CornerRadius> EditorCorners { get; } = new(withToolbar => withToolbar ? new CornerRadius(0, 0, 4, 4) : new CornerRadius(4));

    /// <summary>Folder icon like Thunderbird: own symbols for system folders, a plain folder for all others.</summary>
    public static FuncValueConverter<Neruna.Core.Mail.FolderRole, Geometry?> FolderIcon { get; } = new(role =>
        Resource(role switch
        {
            Neruna.Core.Mail.FolderRole.Inbox => "IconInbox",
            Neruna.Core.Mail.FolderRole.Sent => "IconSend",
            Neruna.Core.Mail.FolderRole.Drafts => "IconEdit",
            Neruna.Core.Mail.FolderRole.Trash => "IconDelete",
            Neruna.Core.Mail.FolderRole.Junk => "IconJunk",
            Neruna.Core.Mail.FolderRole.Archive => "IconArchive",
            _ => "IconFolder",
        }) as Geometry);

    // Einstellungen → News: how a block of a post is shown.
    public static FuncValueConverter<Neruna.Core.News.NewsBlockKind, bool> IsNewsParagraph { get; } = new(k => k == Neruna.Core.News.NewsBlockKind.Paragraph);

    public static FuncValueConverter<Neruna.Core.News.NewsBlockKind, bool> IsNewsHeading { get; } = new(k => k == Neruna.Core.News.NewsBlockKind.Heading);

    public static FuncValueConverter<Neruna.Core.News.NewsBlockKind, bool> IsNewsItem { get; } = new(k => k == Neruna.Core.News.NewsBlockKind.Item);

    /// <summary>An icon geometry from App.axaml by its key.</summary>
    public static FuncValueConverter<string?, Geometry?> Icon { get; } = new(key => key is null ? null : Resource(key) as Geometry);

    private static object? Resource(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value : null;

    /// <summary>Pixels from the top → margin, for elements placed at a time (the "now" line).</summary>
    public static FuncValueConverter<double, Thickness> TopMargin { get; } = new(top => new Thickness(0, top, 0, 0));
}
