using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Neruna.Desktop.ViewModels;
using TheArtOfDev.HtmlRenderer.Avalonia;

namespace Neruna.Desktop.Views;

internal sealed partial class SignaturesView : UserControl
{
    private SignaturesViewModel? _vm;

    public SignaturesView()
    {
        InitializeComponent();
        var preview = this.FindControl<HtmlPanel>("Preview")!;
        preview.BaseStylesheet = MessageBodyView.BaseStylesheet;
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnChanged;
            }

            _vm = DataContext as SignaturesViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnChanged;
                preview.Text = _vm.PreviewHtml;
            }
        };

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SignaturesViewModel.PreviewHtml))
            {
                preview.Text = _vm?.PreviewHtml ?? string.Empty;
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
