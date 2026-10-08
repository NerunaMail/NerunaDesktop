using Avalonia.Controls;
using Avalonia.Interactivity;
using Neruna.Desktop.Editor;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class SignatureEditorWindow : Window
{
    public SignatureEditorWindow()
    {
        InitializeComponent();
        var editor = this.FindControl<HtmlEditor>("Editor")!;
        this.FindControl<TextBox>("NameBox")!.AddHandler(KeyDownEvent, (_, e) => editor.TabIntoFromPreviousField(e), RoutingStrategies.Tunnel);
        DataContextChanged += async (_, _) =>
        {
            if (DataContext is SignatureEditorViewModel vm)
            {
                await vm.AttachEditorAsync(this.FindControl<HtmlEditor>("Editor")!);
            }
        };
    }
}
