using Microsoft.Extensions.Logging;
using Avalonia.Platform.Storage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Neruna.Desktop.Infrastructure;
using Neruna.Desktop.ViewModels;
using TheArtOfDev.HtmlRenderer.Avalonia;
using TheArtOfDev.HtmlRenderer.Core.Entities;

namespace Neruna.Desktop.Views;

/// <summary>
/// Shows the sanitized message HTML in the system web view (WebView2 on Windows, WebKit on macOS and Linux) – the
/// same engine as the editor, so a mail looks as its sender laid it out. No scripts, nothing from the network unless
/// allowed for the message (Content Security Policy, <see cref="MailPaper.ForBrowser"/>); the page never navigates
/// itself – links go through Neruna's check and open outside the app. Where no web view exists (Windows without
/// WebView2, Linux without WebKitGTK) the mail's text is shown, as in the editor's plain-text fallback.
/// </summary>
internal sealed partial class MessageBodyView : UserControl
{
    internal const string BaseStylesheet = MailPaper.LightStylesheet;

    /// <summary>
    /// Snapshot tool only: no web view headless – the HTML renderer draws the mail, so screenshots show it formatted.
    /// Never used in the app.
    /// </summary>
    public static bool UseRenderer { get; set; }

    private static bool _webUnavailable;

    private readonly Border _host;
    private string? _shown;
    private NativeWebView? _web;

    // Pages Neruna gave the web view that have not started loading yet (one per message shown); every other
    // navigation is a link.
    private int _expectedLoads;

    // The mail to write into the empty page once it has loaded; which load that is, and whether it is written.
    private string? _page;
    private int _load;
    private bool _written;
    private MainWindowViewModel? _window;

    // Windows: Neruna's "Bild vergrössern" in the web view's menu (kept, or its handler goes with it).
    private object? _imageMenu;
    private System.Diagnostics.Stopwatch? _shownAt;
    private static readonly System.Net.Http.HttpClient Pictures = new() { Timeout = TimeSpan.FromSeconds(30) };

    public MessageBodyView()
    {
        InitializeComponent();
        _host = this.FindControl<Border>("Host")!;
        DataContextChanged += (_, _) => ShowWhenVisible();
        ActualThemeVariantChanged += (_, _) =>
        {
            _shown = null;
            ShowWhenVisible();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == BoundsProperty)
            {
                ShowWhenVisible();
            }
        };
    }

    // WebView2 hands Ctrl+C/Ctrl+A to the window as accelerator keys instead of copying: done here, with the text
    // marked in the mail (Cmd on macOS).
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _web is null || !ReferenceEquals(_host.Child, _web)
            || (e.KeyModifiers & ~Avalonia.Input.KeyModifiers.Shift) is not (Avalonia.Input.KeyModifiers.Control or Avalonia.Input.KeyModifiers.Meta))
        {
            return;
        }

        if (e.Key == Avalonia.Input.Key.C)
        {
            e.Handled = true;
            _ = CopySelectionAsync(_web);
        }
        else if (e.Key == Avalonia.Input.Key.A)
        {
            e.Handled = true;
            _ = _web.InvokeScript("(function(){var s=window.getSelection();s.removeAllRanges();var r=document.createRange();r.selectNodeContents(document.body);s.addRange(r);return \"ok\";})()");
        }
    }

    private async Task CopySelectionAsync(NativeWebView web)
    {
        var text = await web.InvokeScript("window.getSelection().toString()");
        if (text is { Length: >= 2 } && text[0] == '"')
        {
            text = System.Text.Json.JsonSerializer.Deserialize<string>(text);
        }

        if (!string.IsNullOrEmpty(text) && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowChanged;
            FollowOverlay();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowChanged;
            _window = null;
        }
    }

    private void OnWindowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.HasOverlay))
        {
            FollowOverlay();
        }
    }

    // The system draws the web view above everything of the window, the dimmed dialog included: while a dialog is
    // open it steps aside (as the editor does).
    private void FollowOverlay()
    {
        if (_web is not null && _window is not null)
        {
            _web.IsVisible = !_window.HasOverlay;
        }
    }

    private void ShowWhenVisible()
    {
        var pane = DataContext as ReadingPaneViewModel;
        var html = pane?.Html ?? string.Empty;
        if (!IsEffectivelyVisible || Bounds.Width <= 0 || ReferenceEquals(html, _shown))
        {
            return;
        }

        _shown = html;
        if (UseRenderer)
        {
            ShowWithRenderer(html);
        }
        else if (_webUnavailable || pane is null || !ShowInWebView(pane, html))
        {
            ShowText(pane?.PlainText ?? string.Empty);
        }
    }

    // No web view on this system: the mail's text, selectable.
    private void ShowText(string text)
    {
        _host.Background = Brushes.Transparent;
        _host.Child = new ScrollViewer
        {
            Content = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
        };
    }

    private bool ShowInWebView(ReadingPaneViewModel pane, string html)
    {
        try
        {
            var (page, dark) = MailPaper.ForBrowser(html, MailPaper.IsDarkTheme, pane.AllowsRemoteContent, pane.InlinePictureDataUri);
            _host.Background = dark ? MailPaper.DarkBrush : Brushes.White;
            if (_web is null)
            {
                _web = new NativeWebView();
                _web.EnvironmentRequested += OnEnvironmentRequested;
                _web.NavigationStarted += OnNavigationStarting;
                _web.NavigationCompleted += OnNavigationCompleted;
                _web.WebMessageReceived += OnWebMessage;
                _web.NewWindowRequested += OnNewWindowRequested;
                _web.AdapterCreated += (_, args) => HookImageMenu(args.TryGetPlatformHandle());
            }

            if (!ReferenceEquals(_host.Child, _web))
            {
                _host.Child = _web;
                FollowOverlay();
            }

            _web.Background = _host.Background;
            _page = page;
            _written = false;
            _expectedLoads++;
            _shownAt = System.Diagnostics.Stopwatch.StartNew();
            _web.NavigateToString(MailPaper.BrowserShell(pane.AllowsRemoteContent, dark, ++_load));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No web view on this system (Windows without WebView2, Linux without WebKitGTK).
            _webUnavailable = true;
            _web = null;
            return false;
        }
    }

    private void HookImageMenu(Avalonia.Platform.IPlatformHandle? handle)
    {
        if (_imageMenu is null && OperatingSystem.IsWindows() && handle is Avalonia.Platform.IWindowsWebView2PlatformHandle webView2)
        {
            _imageMenu = Neruna.Desktop.Editor.WebView2ImageMenu.Attach(webView2.CoreWebView2, Neruna.Core.Localization.Texts.T("Bild vergrössern"),
                () => Dispatcher.UIThread.Post(() => _ = MagnifyClickedAsync()),
                ex => CrashHandler.Logger?.LogWarning(ex, "Picture menu of the reading pane failed"));
        }
    }

    // The address of the picture last right-clicked (KeyScript notes it).
    private async Task MagnifyClickedAsync()
    {
        if (_web is null)
        {
            return;
        }

        var source = await _web.InvokeScript("(function(){var i=window.nerunaPicture;return i?(i.currentSrc||i.src||''):'';})()");
        if (source is { Length: >= 2 } && source[0] == '"')
        {
            source = System.Text.Json.JsonSerializer.Deserialize<string>(source);
        }

        if (!string.IsNullOrEmpty(source))
        {
            await MagnifyAsync(source);
        }
    }

    // The picture as the mail has it: embedded (data:), or off the internet only where the mail may load it anyway.
    private async Task MagnifyAsync(string source)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        try
        {
            byte[] data;
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && source.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) is var at and > 0)
            {
                data = Convert.FromBase64String(Uri.UnescapeDataString(source[(at + 8)..]));
            }
            else if (DataContext is ReadingPaneViewModel { AllowsRemoteContent: true } && Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            {
                data = await Pictures.GetByteArrayAsync(uri);
            }
            else
            {
                return;
            }

            using var stream = new MemoryStream(data);
            new ImageViewerWindow(new Avalonia.Media.Imaging.Bitmap(stream)).Show(owner);
        }
        catch (Exception ex) when (ex is FormatException or System.Net.Http.HttpRequestException or TaskCanceledException or ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            // Not a picture Neruna can show (SVG …) or no longer reachable: the menu does nothing.
        }
    }

    // Nothing of a mail stays behind: no cookies or storage from pictures loaded off the internet.
    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = false;
        switch (e)
        {
            case Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs windows:
                windows.IsInPrivateModeEnabled = true;
                break;
            case Avalonia.Platform.AppleWKWebViewEnvironmentRequestedEventArgs apple:
                apple.NonPersistentDataStore = true;
                break;
            case Avalonia.Platform.GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.EphemeralDataManager = true;
                break;
        }
    }

    // The page itself runs no scripts (its policy forbids them); Neruna adds one that hands the mail program's keys
    // (Delete, Ctrl+R …) to the window, which the web view would otherwise keep once clicked into, and reports clicks
    // (the web view then takes the keyboard focus from the message list). Added after every mail written into the
    // page: document.open() drops the listeners.
    private const string KeyScript = """
        (function () {
          function post(message) {
            if (typeof window.invokeCSharpAction === "function") window.invokeCSharpAction(JSON.stringify(message));
          }
          document.addEventListener("mousedown", function () { post({ type: "focus" }); }, true);
          document.addEventListener("contextmenu", function (e) {
            window.nerunaPicture = e.target && e.target.closest ? e.target.closest("img") : null;
          }, true);
          document.addEventListener("keydown", function (e) {
            var command = e.ctrlKey || e.metaKey;
            var key = e.key || "";
            if (/^(Control|Shift|Alt|Meta)$/.test(key)) return;
            if (command && (key === "c" || key === "C" || key === "a" || key === "A")) return;
            if (!command && !e.altKey && !/^(Delete|Escape|F\d+)$/.test(key)) return;
            e.preventDefault();
            post({ type: "key", key: key, ctrl: command, shift: e.shiftKey, alt: e.altKey });
          }, true);
        })();
        """;

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            WritePage(_load);
        }
    }

    // The empty page has loaded (its "ready", or the web view's report – whichever comes first): the mail goes in.
    private void WritePage(int load)
    {
        if (load != _load || _written || _web is null || _page is not { } page)
        {
            return;
        }

        _written = true;
        CrashHandler.Logger?.LogInformation("Mail page ready in the web view after {Elapsed} ms", _shownAt?.ElapsedMilliseconds);
        _ = _web.InvokeScript("document.open();document.write(" + System.Text.Json.JsonSerializer.Serialize(page) + ");document.close();" + KeyScript + ";\"ok\""); // a result WebKitGTK can return (undefined fails there)
    }

    private void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            var body = e.Body ?? "{}";
            if (body.StartsWith('"'))
            {
                body = System.Text.Json.JsonSerializer.Deserialize<string>(body) ?? "{}";
            }

            var message = System.Text.Json.JsonDocument.Parse(body).RootElement;
            if (message.TryGetProperty("type", out var ready) && ready.GetString() == "ready"
                && message.TryGetProperty("load", out var load) && load.TryGetInt32(out var number))
            {
                Dispatcher.UIThread.Post(() => WritePage(number));
                return;
            }

            if (message.TryGetProperty("type", out var kind) && kind.GetString() == "focus")
            {
                // A click into the mail: the window's keys (Ctrl+A, Ctrl+C …) now belong to the mail, not to the list.
                Dispatcher.UIThread.Post(() => _web?.Focus());
                return;
            }

            if (message.TryGetProperty("type", out var type) && type.GetString() == "key"
                && message.TryGetProperty("key", out var name) && name.GetString() is { Length: > 0 } text
                && Enum.TryParse<Avalonia.Input.Key>(text.Length == 1 ? text.ToUpperInvariant() : text, ignoreCase: true, out var key))
            {
                var modifiers = Avalonia.Input.KeyModifiers.None;
                modifiers |= message.TryGetProperty("ctrl", out var ctrl) && ctrl.ValueKind == System.Text.Json.JsonValueKind.True ? Avalonia.Input.KeyModifiers.Control : 0;
                modifiers |= message.TryGetProperty("shift", out var shift) && shift.ValueKind == System.Text.Json.JsonValueKind.True ? Avalonia.Input.KeyModifiers.Shift : 0;
                modifiers |= message.TryGetProperty("alt", out var alt) && alt.ValueKind == System.Text.Json.JsonValueKind.True ? Avalonia.Input.KeyModifiers.Alt : 0;
                Dispatcher.UIThread.Post(() => RaiseEvent(new Avalonia.Input.KeyEventArgs
                {
                    RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
                    Key = key,
                    KeyModifiers = modifiers,
                }));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not a message of Neruna's script.
        }
    }

    // NavigateToString arrives as about:blank or a data: address; anything else is a click on a link.
    private void OnNavigationStarting(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (_expectedLoads > 0 && (e.Request is null || e.Request.Scheme is "about" or "data"))
        {
            _expectedLoads--;
            return;
        }

        e.Cancel = true;
        if (e.Request is { } link)
        {
            Dispatcher.UIThread.Post(() => OpenLink(link.OriginalString));
        }
    }

    // Links in mails carry target="_blank": the web view asks for a new window instead of navigating.
    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Request is { } link)
        {
            Dispatcher.UIThread.Post(() => OpenLink(link.OriginalString));
        }
    }

    // Snapshot tool: the HTML renderer. The panel scrolls itself; given its text while hidden (during "Wird
    // geladen …", size 0), it kept a wrong offset and cut off the top of a freshly downloaded mail. So: a fresh panel
    // per message, filled once visible.
    private void ShowWithRenderer(string html)
    {
        var (shown, dark) = MailPaper.Prepare(html, MailPaper.IsDarkTheme);
        _host.Background = dark ? MailPaper.DarkBrush : Brushes.White;
        var panel = new HtmlPanel
        {
            Background = _host.Background,
            IsContextMenuEnabled = true,
            BaseStylesheet = dark ? MailPaper.DarkStylesheet : MailPaper.LightStylesheet,
        };
        panel.ImageLoad += OnImageLoad;
        panel.StylesheetLoad += OnStylesheetLoad;
        panel.LinkClicked += OnLinkClicked;
        _host.Child = panel;
        panel.Text = shown;
    }

    private void OnImageLoad(object? sender, HtmlRendererRoutedEventArgs<HtmlImageLoadEventArgs> e)
    {
        var args = e.Event;
        if (args.Src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return; // Embedded data URIs are handled by the renderer itself.
        }

        args.Handled = true;
        if (DataContext is not ReadingPaneViewModel pane)
        {
            args.Callback();
            return;
        }

        _ = LoadAsync(pane, args);
    }

    private static async Task LoadAsync(ReadingPaneViewModel pane, HtmlImageLoadEventArgs args)
    {
        var bitmap = await pane.LoadImageAsync(args.Src);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (bitmap is null)
            {
                args.Callback();
            }
            else
            {
                args.Callback(bitmap);
            }
        });
    }

    // Remote stylesheets (@import, <link>) could track the reader; they are never fetched.
    private static void OnStylesheetLoad(object? sender, HtmlRendererRoutedEventArgs<HtmlStylesheetLoadEventArgs> e) =>
        e.Event.SetStyleSheet = string.Empty;

    private void OnLinkClicked(object? sender, HtmlRendererRoutedEventArgs<HtmlLinkClickedEventArgs> e)
    {
        e.Event.Handled = true;
        OpenLink(e.Event.Link);
    }

    private void OpenLink(string link)
    {
        if (link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            var address = Uri.UnescapeDataString(link[7..].Split('?')[0]);
            var mail = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext as MainWindowViewModel;
            _ = mail?.MailPage.ComposeToAsync(address);
            return;
        }

        // A phone number (tel:, e.g. in a signature): the system's telephony program dials it.
        if (Neruna.Core.Mail.PhoneLink.Normalize(link) is { } phone)
        {
            if (TopLevel.GetTopLevel(this) is Window caller)
            {
                _ = caller.Launcher.LaunchUriAsync(new Uri(phone));
            }

            return;
        }

        // A link to a file: only links at all from a safe sender (otherwise shown as text); programs after asking.
        if (Neruna.Core.Mail.FileLinks.IsFileLink(link))
        {
            if (DataContext is ReadingPaneViewModel { OpensFileLinks: true } safe && Neruna.Core.Mail.FileLinks.ToPath(link) is { } path && TopLevel.GetTopLevel(this) is Window owner)
            {
                _ = OpenFileAsync(owner, safe, path);
            }

            return;
        }

        // Only web links leave the mail (an anchor within it, "about:", "data:" stay where they are).
        if (!Uri.TryCreate(link, UriKind.Absolute, out var web) || web.Scheme is not ("https" or "http"))
        {
            return;
        }

        // Checked first (Einstellungen → Datenschutz): shown before opening if suspicious or always, cleaned up if set.
        if (DataContext is ReadingPaneViewModel pane && TopLevel.GetTopLevel(this) is Window window)
        {
            _ = pane.OpenLinkAsync(
                link,
                check => LinkDialog.ShowAsync(window, check),
                target => Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? window.Launcher.LaunchUriAsync(uri) : Task.CompletedTask);
        }
    }

    private static async Task OpenFileAsync(Window owner, ReadingPaneViewModel pane, string path)
    {
        if (Neruna.Core.Mail.FileLinks.IsExecutable(path) && !await LinkDialog.ShowProgramAsync(owner, path))
        {
            return;
        }

        var opened = Directory.Exists(path)
            ? await owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path))
            : File.Exists(path) && await owner.Launcher.LaunchFileInfoAsync(new FileInfo(path));
        if (!opened)
        {
            pane.ShowLinkError(Neruna.Core.Localization.Texts.F("«{0}» ist nicht erreichbar – fehlt die Verbindung zum Netzlaufwerk?", path));
        }
    }
}
