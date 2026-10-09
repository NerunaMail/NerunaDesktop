using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Neruna.Desktop.Infrastructure;
using Neruna.Desktop.ViewModels;
using TheArtOfDev.HtmlRenderer.Avalonia;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

internal sealed partial class TextTemplatesView : UserControl
{
    private TextTemplatesViewModel? _vm;

    public TextTemplatesView()
    {
        InitializeComponent();
        var host = this.FindControl<Border>("PreviewHost")!;
        ActualThemeVariantChanged += (_, _) => Show();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnChanged;
            }

            _vm = DataContext as TextTemplatesViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnChanged;
                Show();
            }
        };

        // Shown like a mail in the reading pane: dark in the dark theme unless the template has its own background.
        void Show()
        {
            var (html, dark) = MailPaper.Prepare(_vm?.PreviewHtml ?? string.Empty, MailPaper.IsDarkTheme);
            var preview = new HtmlPanel
            {
                Background = dark ? MailPaper.DarkBrush : Avalonia.Media.Brushes.White,
                BaseStylesheet = dark ? MailPaper.DarkStylesheet : MailPaper.LightStylesheet,
            };
            host.Background = preview.Background;
            host.Child = preview;
            preview.Text = html;
        }

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TextTemplatesViewModel.PreviewHtml))
            {
                Show();
            }
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm?.EditCommand.CanExecute(null) == true)
        {
            _ = _vm.EditCommand.ExecuteAsync(null);
        }
    }
}
