using System.Globalization;
using System.Runtime.InteropServices;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Editor;

/// <summary>A language of the spell check; <see cref="Code"/> null: the system's.</summary>
internal sealed record SpellLanguage(string? Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The languages the spell check of the editor can be set to. The check itself is the web engine's: WebView2 checks
/// the language its environment was created with (one at a time), WebKitGTK the ones set on its context; on macOS the
/// system picks the language by itself.
/// </summary>
internal static class SpellLanguages
{
    private static readonly string[] Codes = ["de-CH", "de-DE", "de-AT", "en-GB", "en-US", "fr-CH", "fr-FR", "it-CH", "it-IT"];

    public static IReadOnlyList<SpellLanguage> All { get; } =
    [
        new(null, T("Systemsprache")),
        .. Codes.Select(code => new SpellLanguage(code, NameOf(code))),
    ];

    /// <summary>On macOS the system decides: no choice to offer.</summary>
    public static bool CanChoose => !OperatingSystem.IsMacOS();

    public static SpellLanguage Find(string? code) => All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    // The language's own name ("Deutsch (Schweiz)", "English (United Kingdom)").
    private static string NameOf(string code)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(code).NativeName;
            return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.GetCultureInfo(code)) + name[1..] : code;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    // ── WebKitGTK ──

    /// <summary>
    /// Turns the spell check on for the editor's web view and sets its language (null: the system's). WebKitGTK runs on
    /// its own GLib thread, so the call is handed to it.
    /// </summary>
    public static void ApplyGtk(nint webView, string? code)
    {
        if (webView == 0 || !Gtk.Load())
        {
            return;
        }

        var request = GCHandle.Alloc(new GtkRequest(webView, code?.Replace('-', '_')));
        Gtk.Invoke!(0, Gtk.Callback, GCHandle.ToIntPtr(request));
    }

    private sealed record GtkRequest(nint WebView, string? Language);

    private static int OnGlibThread(nint data)
    {
        var handle = GCHandle.FromIntPtr(data);
        try
        {
            var request = (GtkRequest)handle.Target!;
            var context = Gtk.GetContext!(request.WebView);
            if (context != 0)
            {
                Gtk.SetEnabled!(context, 1);
                if (request.Language is { } language)
                {
                    var text = Marshal.StringToCoTaskMemUTF8(language);
                    var list = Marshal.AllocCoTaskMem(2 * IntPtr.Size);
                    try
                    {
                        Marshal.WriteIntPtr(list, text);
                        Marshal.WriteIntPtr(list, IntPtr.Size, 0);
                        Gtk.SetLanguages!(context, list);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(list);
                        Marshal.FreeCoTaskMem(text);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Never into native code: the spell check simply stays as it was.
        }
        finally
        {
            handle.Free();
        }

        return 0; // G_SOURCE_REMOVE
    }

    private static class Gtk
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SourceFunc(nint data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void InvokeFunc(nint context, SourceFunc function, nint data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nint GetContextFunc(nint webView);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SetEnabledFunc(nint context, int enabled);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SetLanguagesFunc(nint context, nint languages);

        // Kept in a static field: native code calls it later, the collector must not take it.
        public static readonly SourceFunc Callback = OnGlibThread;

        private static bool? _loaded;
        public static InvokeFunc? Invoke;
        public static GetContextFunc? GetContext;
        public static SetEnabledFunc? SetEnabled;
        public static SetLanguagesFunc? SetLanguages;

        public static bool Load()
        {
            if (_loaded is { } loaded)
            {
                return loaded;
            }

            if (!OperatingSystem.IsLinux()
                || !NativeLibrary.TryLoad("libglib-2.0.so.0", out var glib)
                || !(NativeLibrary.TryLoad("libwebkit2gtk-4.1.so.0", out var webkit) || NativeLibrary.TryLoad("libwebkit2gtk-4.0.so.37", out webkit))
                || !NativeLibrary.TryGetExport(glib, "g_main_context_invoke", out var invoke)
                || !NativeLibrary.TryGetExport(webkit, "webkit_web_view_get_context", out var getContext)
                || !NativeLibrary.TryGetExport(webkit, "webkit_web_context_set_spell_checking_enabled", out var setEnabled)
                || !NativeLibrary.TryGetExport(webkit, "webkit_web_context_set_spell_checking_languages", out var setLanguages))
            {
                _loaded = false;
                return false;
            }

            Invoke = Marshal.GetDelegateForFunctionPointer<InvokeFunc>(invoke);
            GetContext = Marshal.GetDelegateForFunctionPointer<GetContextFunc>(getContext);
            SetEnabled = Marshal.GetDelegateForFunctionPointer<SetEnabledFunc>(setEnabled);
            SetLanguages = Marshal.GetDelegateForFunctionPointer<SetLanguagesFunc>(setLanguages);
            _loaded = true;
            return true;
        }
    }
}
