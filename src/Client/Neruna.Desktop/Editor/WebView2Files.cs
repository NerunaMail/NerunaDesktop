using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Web.WebView2.Core;

namespace Neruna.Desktop.Editor;

/// <summary>
/// Windows: files dropped into the editor (WebView2) with their paths. The page sends them with
/// <c>chrome.webview.postMessageWithAdditionalObjects</c>; Avalonia's WebView passes on only the text of a message,
/// so the CoreWebView2 behind it is asked directly. Kept apart so the WebView2 library loads only on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WebView2Files
{
    // The wrapper must live as long as the editor, or its event handler goes with it.
    private readonly CoreWebView2 _core;

    private WebView2Files(CoreWebView2 core, Action<string, IReadOnlyList<string>> dropped)
    {
        _core = core;
        _core.WebMessageReceived += (_, e) =>
        {
            if (e.AdditionalObjects is not { Count: > 0 } objects)
            {
                return;
            }

            var paths = new List<string>();
            for (var i = 0; i < objects.Count; i++)
            {
                if (objects[i] is CoreWebView2File { Path: { Length: > 0 } path })
                {
                    paths.Add(path);
                }
            }

            string message;
            try
            {
                message = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return;
            }

            dropped(message, paths);
        };
    }

    /// <returns>Null if the WebView is no WebView2 or its wrapper cannot be created.</returns>
    public static WebView2Files? Attach(IntPtr coreWebView2, Action<string, IReadOnlyList<string>> dropped)
    {
        try
        {
            return coreWebView2 == IntPtr.Zero ? null : new WebView2Files(CoreWebView2.CreateFromComICoreWebView2(coreWebView2), dropped);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or FileNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>Mouse and keys while dragging files from outside (Windows; elsewhere nothing is known).</summary>
internal static class DragKeys
{
    private const int VkLeftButton = 0x01;
    private const int VkRightButton = 0x02;
    private const int VkControl = 0x11;
    private const int SmSwapButton = 23;

    /// <summary>The secondary (usually right) mouse button is held: after dropping, Neruna asks what to do.</summary>
    public static bool IsSecondaryButtonDown() =>
        OperatingSystem.IsWindows() && IsDown(NativeMethods.GetSystemMetrics(SmSwapButton) != 0 ? VkLeftButton : VkRightButton);

    public static bool IsControlDown() => OperatingSystem.IsWindows() && IsDown(VkControl);

    // GetAsyncKeyState sees the physical buttons, whichever window has the mouse.
    private static bool IsDown(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);
    }
}
