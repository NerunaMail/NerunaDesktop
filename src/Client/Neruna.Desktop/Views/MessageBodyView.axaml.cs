using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Neruna.Desktop.ViewModels;
using TheArtOfDev.HtmlRenderer.Avalonia;
using TheArtOfDev.HtmlRenderer.Core.Entities;

namespace Neruna.Desktop.Views;

/// <summary>
/// Renders the sanitized message HTML. Images are resolved by the view model (inline parts; remote only when allowed),
/// external stylesheets are never loaded, links open outside the app.
/// </summary>
internal sealed partial class MessageBodyView : UserControl
{
    internal const string BaseStylesheet =
        "body { font-family: 'Segoe UI', Inter, Arial, sans-serif; font-size: 10.5pt; color: #1b1b1b; margin: 0; }" +
        " a { color: #0F6CBD; } blockquote { border-left: 2px solid #C8C8C8; margin-left: 4px; padding-left: 10px; color: #424242; }" +
        " pre, code { font-family: Consolas, 'Cascadia Mono', monospace; }";

    private readonly Border _host;
    private string? _shown;

    public MessageBodyView()
    {
        InitializeComponent();
        _host = this.FindControl<Border>("Host")!;
        DataContextChanged += (_, _) => ShowWhenVisible();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == BoundsProperty)
            {
                ShowWhenVisible();
            }
        };
    }

    // The panel scrolls itself. Given its text while hidden (during "Wird geladen …", size 0), it kept a wrong
    // offset and cut off the top of a freshly downloaded mail. So: a fresh panel per message, filled once visible.
    private void ShowWhenVisible()
    {
        var html = (DataContext as ReadingPaneViewModel)?.Html ?? string.Empty;
        if (!IsEffectivelyVisible || Bounds.Width <= 0 || ReferenceEquals(html, _shown))
        {
            return;
        }

        _shown = html;
        var panel = new HtmlPanel
        {
            Background = Brushes.White,
            IsContextMenuEnabled = true,
            BaseStylesheet = BaseStylesheet,
        };
        panel.ImageLoad += OnImageLoad;
        panel.StylesheetLoad += OnStylesheetLoad;
        panel.LinkClicked += OnLinkClicked;
        _host.Child = panel;
        panel.Text = html;
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
        var link = e.Event.Link;
        if (link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            var address = Uri.UnescapeDataString(link[7..].Split('?')[0]);
            var mail = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext as MainWindowViewModel;
            _ = mail?.MailPage.ComposeToAsync(address);
            return;
        }

        if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
        }
    }
}
