using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Web.WebView2.Core;

namespace Neruna.Desktop.Editor;

/// <summary>
/// Windows: the reading pane's right-click menu on a picture. The web view's own magnifier ("Bild vergrössern" under
/// "Weitere Tools") is a small popup whose size cannot be changed; it gives way to Neruna's, which opens the picture
/// in a window of its own. Kept apart so the WebView2 library loads only on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WebView2ImageMenu
{
    // The wrapper must live as long as the web view, or its event handler goes with it.
    private readonly CoreWebView2 _core;

    private WebView2ImageMenu(CoreWebView2 core, string label, Action<string> magnify)
    {
        _core = core;
        _core.ContextMenuRequested += (_, e) =>
        {
            RemoveMagnifier(e.MenuItems);
            if (e.ContextMenuTarget is { Kind: CoreWebView2ContextMenuTargetKind.Image, HasSourceUri: true } target)
            {
                var source = target.SourceUri;
                var item = _core.Environment.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command);
                item.CustomItemSelected += (_, _) => magnify(source);
                e.MenuItems.Insert(0, item);
            }
        };
    }

    // By its name (the label is in the language of Windows); a submenu left empty ("Weitere Tools") goes too.
    private static void RemoveMagnifier(IList<CoreWebView2ContextMenuItem> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item.Name.Contains("magnif", StringComparison.OrdinalIgnoreCase))
            {
                items.RemoveAt(i);
            }
            else if (item.Kind == CoreWebView2ContextMenuItemKind.Submenu)
            {
                RemoveMagnifier(item.Children);
                if (item.Children.Count == 0)
                {
                    items.RemoveAt(i);
                }
            }
        }
    }

    /// <returns>Null if the web view is no WebView2 or its wrapper cannot be created.</returns>
    public static WebView2ImageMenu? Attach(IntPtr coreWebView2, string label, Action<string> magnify)
    {
        try
        {
            return coreWebView2 == IntPtr.Zero ? null : new WebView2ImageMenu(CoreWebView2.CreateFromComICoreWebView2(coreWebView2), label, magnify);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or FileNotFoundException)
        {
            return null;
        }
    }
}
