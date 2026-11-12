using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Interactivity;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

/// <summary>Font, size, emphasis, colors, lists, alignment, link, image – shared by compose and the signature editor.</summary>
internal sealed partial class FormattingToolbar : UserControl
{
    /// <summary>The spell check's language in the bar too (notes); the compose form has it under "Mehr".</summary>
    public static readonly StyledProperty<bool> ShowSpellLanguageProperty = AvaloniaProperty.Register<FormattingToolbar, bool>(nameof(ShowSpellLanguage));

    // What goes into "⋯" first when the line gets too narrow: each step a group in the bar and its place in the menu.
    private readonly (Control[] Bar, Control[] Menu)[] _steps;

    private int _level = -1;

    public FormattingToolbar()
    {
        InitializeComponent();
        _steps =
        [
            ([SpellGroup], [MenuSpell]),
            ([ClearButton], [MenuClear]),
            ([IndentGroup], [MenuIndent]),
            ([InsertGroup], [MenuInsert]),
            ([AlignGroup], [MenuAlign]),
            ([StrikeButton, HighlightButton], [MenuStrike, MenuHighlight]),
            ([ListGroup], [MenuLists]),
            ([FontBox], [MenuFontBox]),
            ([ColorGroup], [MenuTextColor]),
        ];
        SizeChanged += (_, _) => Fit();
        Apply(0);
    }

    public bool ShowSpellLanguage
    {
        get => GetValue(ShowSpellLanguageProperty);
        set => SetValue(ShowSpellLanguageProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowSpellLanguageProperty)
        {
            _level = -1;
            Fit();
        }
    }

    /// <summary>
    /// One line at any width: as many groups as fit (measured, so fonts and languages count), the rest in "⋯" – never
    /// wrapped, scrolled or cut off.
    /// </summary>
    private void Fit()
    {
        var border = (Border)Content!;
        var available = Bounds.Width - border.Padding.Left - border.Padding.Right - border.BorderThickness.Left - border.BorderThickness.Right;
        if (available <= 0)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var level = 0;
        for (; level <= _steps.Length; level++)
        {
            Apply(level);
            Bar.Measure(Size.Infinity);
            if (Bar.DesiredSize.Width <= available)
            {
                break;
            }
        }

        // A button that just went into the menu had the focus: on to "⋯" instead of nowhere.
        if (focused is not null && Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(Bar, focused) && !focused.IsEffectivelyVisible)
        {
            MoreButton.Focus();
        }
    }

    private void Apply(int level)
    {
        level = Math.Min(level, _steps.Length);
        if (level == _level)
        {
            return;
        }

        _level = level;
        var spell = ShowSpellLanguage && Editor.FormattingViewModel.CanChooseSpellLanguage;
        for (var i = 0; i < _steps.Length; i++)
        {
            var inMenu = i < level;
            var shown = i > 0 || spell; // the first step: only where the language is wanted at all
            foreach (var control in _steps[i].Bar)
            {
                control.IsVisible = shown && !inMenu;
            }

            foreach (var control in _steps[i].Menu)
            {
                control.IsVisible = shown && inMenu;
            }
        }

        FontSection.IsVisible = MenuFontBox.IsVisible || MenuStrike.IsVisible || MenuTextColor.IsVisible || MenuClear.IsVisible;
        ParagraphSection.IsVisible = MenuLists.IsVisible || MenuIndent.IsVisible || MenuAlign.IsVisible;
        InsertSection.IsVisible = MenuInsert.IsVisible;
        MoreButton.IsVisible = level > (spell ? 0 : 1);
    }

    // In "⋯": a color, an action – applied, and the menu closes.
    private void OnMenuTextColorPicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { DataContext: string color } && DataContext is Editor.FormattingViewModel formatting)
        {
            e.Handled = true;
            formatting.ApplyTextColorCommand.Execute(color);
        }

        MoreButton.Flyout?.Hide();
    }

    private void OnMenuHighlightPicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Editor.FormattingViewModel formatting)
        {
            e.Handled = true;
            formatting.ApplyHighlightCommand.Execute((e.Source as Button)?.DataContext as string);
        }

        MoreButton.Flyout?.Hide();
    }

    private void OnMenuItemClick(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() => MoreButton.Flyout?.Hide());

    private void OnMenuLinkKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnMenuInsertLink(sender, e);
        }
    }

    private void OnMenuInsertLink(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is Editor.FormattingViewModel formatting)
        {
            formatting.InsertLinkCommand.Execute(null);
        }

        MoreButton.Flyout?.Hide();
    }

    // Escape closes the menu (also from a field inside it).
    private void OnMoreKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            MoreButton.Flyout?.Hide();
            MoreButton.Focus();
        }
    }

    // Picking a swatch applies the color (via the command) and closes the palette.
    // Applied here, not by the swatch's Command: Click comes before the Command, and closing the palette detaches the
    // swatch – its $parent binding drops the Command before Avalonia runs it, so the color never changed.
    private void OnTextColorPicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { DataContext: string color } && DataContext is Editor.FormattingViewModel formatting)
        {
            e.Handled = true;
            formatting.ApplyTextColorCommand.Execute(color);
        }

        this.FindControl<SplitButton>("TextColorButton")?.Flyout?.Hide();
    }

    private void OnHighlightPicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Editor.FormattingViewModel formatting)
        {
            e.Handled = true;
            // A swatch carries its color; "Keine Farbe" none.
            formatting.ApplyHighlightCommand.Execute((e.Source as Button)?.DataContext as string);
        }

        this.FindControl<SplitButton>("HighlightButton")?.Flyout?.Hide();
    }

    // The field is empty (the hint shows the forms): ready for typing or pasting at once.
    private void OnLinkFlyoutOpened(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => ((sender as Flyout)?.Content as Panel)?.Children.OfType<TextBox>().FirstOrDefault()?.Focus(),
            DispatcherPriority.Input);

    private void OnLinkUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            InsertLink();
        }
    }

    private void OnInsertLink(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        InsertLink();
    }

    private void InsertLink()
    {
        if (DataContext is Editor.FormattingViewModel formatting)
        {
            formatting.InsertLinkCommand.Execute(null);
        }

        this.FindControl<Button>("LinkButton")?.Flyout?.Hide();
    }
}
