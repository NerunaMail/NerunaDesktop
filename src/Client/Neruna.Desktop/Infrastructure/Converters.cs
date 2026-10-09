using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Neruna.Contracts.Discovery;

namespace Neruna.Desktop;

internal static class Converters
{
    public static FuncValueConverter<SocketSecurity, string> SecurityLabel { get; } = new(security => security switch
    {
        SocketSecurity.SslOnConnect => "SSL/TLS",
        SocketSecurity.StartTls => "STARTTLS",
        _ => "Keine (unverschlüsselt)",
    });

    /// <summary>"#RRGGBB" → brush, for color swatches.</summary>
    public static FuncValueConverter<string?, IBrush> ColorBrush { get; } = new(color =>
        Color.TryParse(color, out var parsed) ? new SolidColorBrush(parsed) : Brushes.Transparent);

    public static FuncValueConverter<bool, FontWeight> BoldIf { get; } = new(bold => bold ? FontWeight.SemiBold : FontWeight.Normal);

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

    /// <summary>An icon geometry from App.axaml by its key.</summary>
    public static FuncValueConverter<string?, Geometry?> Icon { get; } = new(key => key is null ? null : Resource(key) as Geometry);

    private static object? Resource(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value : null;

    /// <summary>Pixels from the top → margin, for elements placed at a time (the "now" line).</summary>
    public static FuncValueConverter<double, Thickness> TopMargin { get; } = new(top => new Thickness(0, top, 0, 0));
}
