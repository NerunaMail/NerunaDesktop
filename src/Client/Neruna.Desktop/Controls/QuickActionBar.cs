using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Neruna.Desktop.ViewModels;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Controls;

/// <summary>
/// The quick actions for the open mail, under its header: a flash, then one coloured button per action. They wrap onto
/// at most <see cref="MaxLines"/> lines; what does not fit is behind «+N weitere» at the end of the last line.
/// </summary>
internal sealed class QuickActionBar : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<QuickActionButton>?> ActionsProperty =
        AvaloniaProperty.Register<QuickActionBar, IReadOnlyList<QuickActionButton>?>(nameof(Actions));

    private const double Spacing = 6;
    private const double IconWidth = 22;

    private readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal, ItemSpacing = Spacing, LineSpacing = Spacing };
    private readonly Button _more = new() { Classes = { "command" }, Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center };
    private readonly MenuFlyout _moreMenu = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private readonly List<Button> _buttons = [];
    private double _laidOutFor = -1;

    public QuickActionBar()
    {
        _more.Flyout = _moreMenu;
        var icon = new PathIcon { Width = 14, Height = 14, Margin = new Thickness(0, 6, IconWidth - 14, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable("IconFlash"));
        icon.Bind(PathIcon.ForegroundProperty, icon.GetResourceObservable("SubtleTextBrush"));
        ToolTip.SetTip(icon, T("Schnellaktionen für diese Mail"));
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(_panel);
        Content = dock;
    }

    public IReadOnlyList<QuickActionButton>? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>Lines of buttons before the rest goes into «+N weitere».</summary>
    public int MaxLines { get; set; } = 2;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActionsProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _panel.Children.Clear();
        _buttons.Clear();
        foreach (var action in Actions ?? [])
        {
            var button = new Button
            {
                Classes = { "quickAction" },
                Content = action.Label,
                Command = action.Command,
                CommandParameter = action.Action,
                Background = Color.TryParse(action.Color, out var color) ? new SolidColorBrush(color) : Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(button, action.Hint);
            _buttons.Add(button);
            _panel.Children.Add(button);
        }

        _panel.Children.Add(_more);
        IsVisible = _buttons.Count > 0;
        _laidOutFor = -1;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width - IconWidth;
        if (!double.IsInfinity(width) && Math.Abs(width - _laidOutFor) > 0.5)
        {
            _laidOutFor = width;
            Fit(width);
        }

        return base.MeasureOverride(availableSize);
    }

    // Greedy, as the wrap panel places them: whole buttons line by line; if not all fit into the lines, the last line
    // keeps room for «+N weitere».
    private void Fit(double width)
    {
        foreach (var button in _buttons)
        {
            button.IsVisible = true;
            button.Measure(Size.Infinity);
        }

        var shown = CountFitting(width, reserve: 0);
        if (shown < _buttons.Count)
        {
            _more.Content = F("+{0} weitere", _buttons.Count - shown);
            _more.Measure(Size.Infinity);
            shown = CountFitting(width, _more.DesiredSize.Width + Spacing);
            _more.Content = F("+{0} weitere", _buttons.Count - shown);
        }

        for (var i = 0; i < _buttons.Count; i++)
        {
            _buttons[i].IsVisible = i < shown;
        }

        _more.IsVisible = shown < _buttons.Count;
        _moreMenu.Items.Clear();
        foreach (var button in _buttons.Skip(shown))
        {
            var item = new MenuItem { Header = button.Content, Command = button.Command, CommandParameter = button.CommandParameter };
            item.Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = button.Background };
            ToolTip.SetTip(item, ToolTip.GetTip(button));
            _moreMenu.Items.Add(item);
        }
    }

    private int CountFitting(double width, double reserve)
    {
        var line = 1;
        var used = 0.0;
        for (var i = 0; i < _buttons.Count; i++)
        {
            var need = _buttons[i].DesiredSize.Width + (used > 0 ? Spacing : 0);
            var limit = line == MaxLines ? width - reserve : width;
            if (used + need <= limit || used == 0 && line < MaxLines)
            {
                used += need;
                continue;
            }

            if (line == MaxLines)
            {
                return i;
            }

            line++;
            used = _buttons[i].DesiredSize.Width;
            if (line == MaxLines && used > width - reserve)
            {
                return i;
            }
        }

        return _buttons.Count;
    }
}
