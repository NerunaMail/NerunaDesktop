using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Controls;

/// <summary>
/// <c>SearchBox.Clearable="True"</c> on a search field: a small × on the right while something is typed; a click
/// empties the field (the search follows its text) and leaves the caret in it for the next term.
/// </summary>
public static class SearchBox
{
    public static readonly AttachedProperty<bool> ClearableProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Clearable", typeof(SearchBox));

    static SearchBox() => ClearableProperty.Changed.AddClassHandler<TextBox>((box, e) =>
    {
        if (e.NewValue is true)
        {
            box.InnerRightContent = ClearButton(box);
        }
    });

    public static bool GetClearable(TextBox box) => box.GetValue(ClearableProperty);

    public static void SetClearable(TextBox box, bool value) => box.SetValue(ClearableProperty, value);

    private static Button ClearButton(TextBox box)
    {
        var button = new Button
        {
            Classes = { "command" },
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
            Content = new PathIcon
            {
                Data = Application.Current?.FindResource("IconClose") as Geometry,
                Width = 10,
                Height = 10,
                Margin = new Thickness(0),
            },
        };
        ToolTip.SetTip(button, T("Suchbegriff löschen"));
        button.IsVisible = !string.IsNullOrEmpty(box.Text);
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                button.IsVisible = !string.IsNullOrEmpty(box.Text);
            }
        };
        button.Click += (_, e) =>
        {
            e.Handled = true;
            box.Clear();
            box.Focus();
        };
        return button;
    }
}
