using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Neruna.Core.Mail;

namespace Neruna.Desktop.Editor;

/// <summary>Formatting at the caret, reported by the editor for the toolbar.</summary>
internal sealed record EditorState(
    bool Bold,
    bool Italic,
    bool Underline,
    bool Strikethrough,
    bool BulletList,
    bool NumberedList,
    string Font,
    double Size,
    string Color);

/// <summary>What the compose view model needs from the editor, independent of how it is implemented.</summary>
internal interface IHtmlEditor
{
    event EventHandler<EditorState>? StateChanged;

    event EventHandler? SendRequested;

    /// <summary>Ctrl+S inside the editor (the web view keeps the key from the window).</summary>
    event EventHandler? SaveRequested;

    /// <summary>The user changed the text (typing, pasting, formatting) – not raised when content is loaded.</summary>
    event EventHandler? ContentChanged;

    /// <summary>Raised once it is clear whether formatting is available (true) or the plain-text fallback is used (false).</summary>
    event EventHandler<bool>? ModeChanged;

    /// <summary>False while loading and when the plain-text fallback is in use.</summary>
    bool IsRich { get; }

    /// <summary>True once the plain-text fallback has replaced the WebView.</summary>
    bool IsPlainText { get; }

    void Load(string html, string font, double sizePt, string placeholder);

    /// <summary>The formatted body, or null in plain-text fallback mode (then use <see cref="GetPlainText"/>).</summary>
    Task<string?> GetHtmlAsync();

    /// <summary>Like <see cref="GetHtmlAsync"/>, but without the default-font wrapper.</summary>
    Task<string?> GetBodyHtmlAsync();

    string GetPlainText();

    Task RunAsync(string script);

    void FocusEditor();
}

/// <summary>
/// HTML mail editor: <c>editor.html</c> (contenteditable) in the system WebView – WebView2 on Windows,
/// WebKit on macOS/Linux. Falls back to a plain text box where no WebView is available, so composing never breaks.
/// The page can never navigate away or open windows; links in quoted mails are inert.
/// </summary>
internal sealed class HtmlEditor : UserControl, IHtmlEditor
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(12);
    private static readonly Lazy<string> EditorPage = new(() =>
    {
        using var stream = typeof(HtmlEditor).Assembly.GetManifestResourceStream("Neruna.Desktop.Editor.editor.html")
                           ?? throw new InvalidOperationException("editor.html resource missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private readonly Panel _host = new();
    private NativeWebView? _web;
    private TextBox? _fallback;

    // Loading content is no user change: TextChanged may arrive later, so compare with what was loaded.
    private string? _loadedText;
    private bool _ready;
    private bool _navigated;
    private (string Html, string Font, double Size, string Placeholder)? _content;

    public HtmlEditor()
    {
        Content = _host;
        Focusable = true;
    }

    /// <summary>Moves keyboard focus into the text when <paramref name="e"/> is Tab (without modifiers); for the field before the editor.</summary>
    public void TabIntoFromPreviousField(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            FocusEditor();
        }
    }

    // Reaching the control by keyboard navigation puts the caret into the text, not onto the frame.
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (ReferenceEquals(e.Source, this))
        {
            FocusEditor();
        }
    }

    public event EventHandler<EditorState>? StateChanged;

    public event EventHandler? SendRequested;

    public event EventHandler? SaveRequested;

    public event EventHandler? ContentChanged;

    public event EventHandler<bool>? ModeChanged;

    /// <summary>Set by the snapshot tool and tests: headless rendering has no native WebView.</summary>
    public static bool ForcePlainText { get; set; }

    public bool IsRich => _ready && _fallback is null;

    public bool IsPlainText => _fallback is not null;

    public void Load(string html, string font, double sizePt, string placeholder)
    {
        _content = (html, font, sizePt, placeholder);
        if (_fallback is not null)
        {
            _loadedText = PlainTextOf(html);
            _fallback.Text = _loadedText;
        }
        else if (_ready)
        {
            _ = PushContentAsync();
        }
    }

    public async Task<string?> GetHtmlAsync()
    {
        if (!IsRich || _web is null)
        {
            return null;
        }

        return Unwrap(await _web.InvokeScript("neruna.getHtml()"));
    }

    public async Task<string?> GetBodyHtmlAsync()
    {
        if (!IsRich || _web is null)
        {
            return null;
        }

        return Unwrap(await _web.InvokeScript("neruna.getBodyHtml()"));
    }

    public string GetPlainText() => _fallback?.Text ?? string.Empty;

    public async Task RunAsync(string script)
    {
        if (IsRich && _web is not null)
        {
            await _web.InvokeScript(script);
        }
    }

    public void FocusEditor()
    {
        if (_fallback is not null)
        {
            _fallback.Focus();
        }
        else
        {
            _web?.Focus();
            _ = RunAsync("neruna.focus()");
        }
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_web is null && _fallback is null)
        {
            Start();
        }
    }

    private void Start()
    {
        if (ForcePlainText)
        {
            UseFallback();
            return;
        }

        try
        {
            _web = new NativeWebView();
            _web.WebMessageReceived += OnWebMessage;
            _web.NavigationStarted += OnNavigationStarting;
            _web.NewWindowRequested += (_, args) => args.Handled = true;
            _host.Children.Add(_web);
            _web.NavigateToString(EditorPage.Value);
            DispatcherTimer.RunOnce(() =>
            {
                if (!_ready)
                {
                    UseFallback();
                }
            }, StartupTimeout);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No WebView runtime (e.g. Windows without WebView2, Linux without WebKitGTK).
            UseFallback();
        }
    }

    // Only the editor page itself; never follow links or redirects inside the editor.
    private void OnNavigationStarting(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (_navigated)
        {
            e.Cancel = true;
        }

        _navigated = true;
    }

    private void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        JsonElement message;
        try
        {
            message = JsonDocument.Parse(Unwrap(e.Body) ?? "{}").RootElement;
        }
        catch (JsonException)
        {
            return;
        }

        switch (message.TryGetProperty("type", out var type) ? type.GetString() : null)
        {
            case "ready":
                _ready = true;
                _ = PushContentAsync();
                ModeChanged?.Invoke(this, true);
                break;
            case "send":
                SendRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "save":
                SaveRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "changed":
                ContentChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "state":
                StateChanged?.Invoke(this, new EditorState(
                    Bool(message, "bold"),
                    Bool(message, "italic"),
                    Bool(message, "underline"),
                    Bool(message, "strikethrough"),
                    Bool(message, "bulletList"),
                    Bool(message, "numberedList"),
                    message.TryGetProperty("font", out var font) ? font.GetString() ?? string.Empty : string.Empty,
                    message.TryGetProperty("size", out var size) && size.TryGetDouble(out var pt) ? pt : 0,
                    message.TryGetProperty("color", out var color) ? color.GetString() ?? "#000000" : "#000000"));
                break;
        }
    }

    private async Task PushContentAsync()
    {
        if (_web is null || _content is not { } content)
        {
            return;
        }

        var script = $"neruna.setContent({Js(content.Html)}, {Js(content.Font)}, {content.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {Js(content.Placeholder)})";
        await _web.InvokeScript(script);
    }

    private static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);

    private void UseFallback()
    {
        if (_fallback is not null)
        {
            return;
        }

        _host.Children.Clear();
        _web = null;
        _loadedText = _content is { } c ? PlainTextOf(c.Html) : string.Empty;
        _fallback = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            Text = _loadedText,
        };
        _fallback.TextChanged += (_, _) =>
        {
            if (Normalize(_fallback.Text) != Normalize(_loadedText))
            {
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        _host.Children.Add(_fallback);
        ModeChanged?.Invoke(this, false);
    }

    // Script results/messages may arrive JSON-encoded (WebView2) or raw, depending on the platform.
    private static string? Unwrap(string? value)
    {
        if (value is { Length: >= 2 } && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value);
            }
            catch (JsonException)
            {
                return value;
            }
        }

        return value;
    }

    // Keeps the empty lines above a signature, which plain-text conversion would trim.
    private static string PlainTextOf(string html)
    {
        var leading = 0;
        while (html.AsSpan(leading * EmptyLine.Length).StartsWith(EmptyLine, StringComparison.Ordinal))
        {
            leading++;
        }

        return new string('\n', leading) + HtmlText.ToPlainText(html);
    }

    private const string EmptyLine = "<div><br></div>";

    private static string Js(string value) => JsonSerializer.Serialize(value);

    private static bool Bool(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
