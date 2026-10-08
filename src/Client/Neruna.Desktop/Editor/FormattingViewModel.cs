using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Desktop.Infrastructure;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Editor;

/// <summary>
/// The formatting toolbar for an <see cref="IHtmlEditor"/> (compose and signature editor): sends commands to the editor
/// and mirrors the formatting at the caret.
/// </summary>
internal sealed partial class FormattingViewModel(IFileService files) : ViewModelBase
{
    public static readonly IReadOnlyList<string> TextColors =
    [
        "#000000", "#FFFFFF", "#E7E6E6", "#44546A", "#4472C4", "#ED7D31", "#A5A5A5", "#FFC000", "#5B9BD5", "#70AD47",
        "#7F7F7F", "#F2F2F2", "#AEAAAA", "#D6DCE4", "#D9E2F3", "#FBE5D5", "#EDEDED", "#FFF2CC", "#DEEBF6", "#E2EFD9",
        "#595959", "#D8D8D8", "#757070", "#ADB9CA", "#B4C6E7", "#F7CBAC", "#DBDBDB", "#FFE598", "#BDD7EE", "#C5E0B3",
        "#262626", "#BFBFBF", "#3A3838", "#323F4F", "#2F5496", "#C55A11", "#7B7B7B", "#BF9000", "#2E75B5", "#538135",
        "#C00000", "#FF0000", "#FFC000", "#FFFF00", "#92D050", "#00B050", "#00B0F0", "#0070C0", "#002060", "#7030A0",
    ];

    public static readonly IReadOnlyList<string> HighlightColors =
    [
        "#FFFF00", "#00FF00", "#00FFFF", "#FF00FF", "#0000FF", "#FF0000", "#000080", "#008080",
        "#008000", "#800080", "#800000", "#808000", "#808080", "#C0C0C0", "#000000",
    ];

    private const long MaxImageBytes = 2 * 1024 * 1024;
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    private IHtmlEditor? _editor;
    private bool _applyingState;

    /// <summary>Raised with a user-facing message, e.g. when an image is too large.</summary>
    public event EventHandler<string>? Problem;

    [ObservableProperty]
    public partial FontChoice? SelectedFont { get; set; }

    /// <summary>Shown when the font at the caret is not in the list (e.g. from a quoted mail).</summary>
    [ObservableProperty]
    public partial string CurrentFontName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double? SelectedSize { get; set; }

    [ObservableProperty]
    public partial string CurrentSizeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBold { get; set; }

    [ObservableProperty]
    public partial bool IsItalic { get; set; }

    [ObservableProperty]
    public partial bool IsUnderline { get; set; }

    [ObservableProperty]
    public partial bool IsStrikethrough { get; set; }

    [ObservableProperty]
    public partial bool IsBulletList { get; set; }

    [ObservableProperty]
    public partial bool IsNumberedList { get; set; }

    /// <summary>Last color applied with the "A" button, shown as its underline (as in word processors).</summary>
    [ObservableProperty]
    public partial string LastTextColor { get; set; } = "#C00000";

    [ObservableProperty]
    public partial string LastHighlightColor { get; set; } = "#FFFF00";

    [ObservableProperty]
    public partial string LinkUrl { get; set; } = "https://";

    /// <summary>False when no WebView is available: the toolbar is hidden and the text is plain.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlainTextFallback))]
    public partial bool IsRichText { get; set; } = true;

    public bool IsPlainTextFallback => !IsRichText;

    public IHtmlEditor? Editor => _editor;

    /// <summary>Connects the toolbar to an editor and loads the content with the given default font.</summary>
    public void Attach(IHtmlEditor editor, string html, string font, double sizePt, string placeholder)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        editor.StateChanged += (_, state) => ApplyState(state);
        editor.ModeChanged += (_, rich) => IsRichText = rich;
        IsRichText = !editor.IsPlainText;
        ApplyState(new EditorState(false, false, false, false, false, false, font, sizePt, "#000000"));
        editor.Load(html, font, sizePt, placeholder);
    }

    public Task RunAsync(string script) => _editor?.RunAsync(script) ?? Task.CompletedTask;

    public static string Js(string value) => JsonSerializer.Serialize(value);

    [RelayCommand]
    private Task Format(string command) => RunAsync($"neruna.exec({Js(command)})");

    [RelayCommand]
    private Task ApplyTextColor(string color)
    {
        LastTextColor = color;
        return RunAsync($"neruna.color({Js(color)})");
    }

    [RelayCommand]
    private Task ApplyHighlight(string? color)
    {
        if (color is not null)
        {
            LastHighlightColor = color;
        }

        return RunAsync($"neruna.highlight({Js(color ?? "transparent")})");
    }

    [RelayCommand]
    private Task InsertLink()
    {
        var url = LinkUrl.Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto"
            ? RunAsync($"neruna.link({Js(uri.AbsoluteUri)})")
            : Task.CompletedTask;
    }

    /// <summary>Embeds a picture (e.g. a logo) as data: URI; it is sent as embedded attachment.</summary>
    [RelayCommand]
    private async Task InsertImageAsync()
    {
        foreach (var path in await files.PickFilesAsync("Bild einfügen"))
        {
            if (!ImageTypes.TryGetValue(Path.GetExtension(path), out var mimeType))
            {
                Problem?.Invoke(this, $"«{Path.GetFileName(path)}» ist kein unterstütztes Bild (PNG, JPG, GIF, WebP).");
                continue;
            }

            if (new FileInfo(path).Length > MaxImageBytes)
            {
                Problem?.Invoke(this, $"«{Path.GetFileName(path)}» ist grösser als 2 MB – bitte verkleinern.");
                continue;
            }

            var data = await File.ReadAllBytesAsync(path);
            await RunAsync($"neruna.insertImage({Js($"data:{mimeType};base64,{Convert.ToBase64String(data)}")})");
        }
    }

    partial void OnSelectedFontChanged(FontChoice? value)
    {
        if (!_applyingState && value is { IsFont: true })
        {
            CurrentFontName = value.Name;
            _ = RunAsync($"neruna.font({Js(value.Name)})");
        }
    }

    partial void OnSelectedSizeChanged(double? value)
    {
        if (!_applyingState && value is { } size)
        {
            CurrentSizeText = size.ToString(CultureInfo.CurrentCulture);
            _ = RunAsync($"neruna.size({size.ToString(CultureInfo.InvariantCulture)})");
        }
    }

    // Moving the caret updates the toolbar without sending the values back to the editor.
    private void ApplyState(EditorState state)
    {
        _applyingState = true;
        try
        {
            IsBold = state.Bold;
            IsItalic = state.Italic;
            IsUnderline = state.Underline;
            IsStrikethrough = state.Strikethrough;
            IsBulletList = state.BulletList;
            IsNumberedList = state.NumberedList;
            SelectedFont = FontCatalog.Find(state.Font);
            CurrentFontName = state.Font;
            SelectedSize = FontCatalog.Sizes.Contains(state.Size) ? state.Size : null;
            CurrentSizeText = state.Size > 0 ? state.Size.ToString(CultureInfo.CurrentCulture) : string.Empty;
        }
        finally
        {
            _applyingState = false;
        }
    }
}
