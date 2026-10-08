using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Neruna.Core.Contacts;
using Neruna.Desktop.Infrastructure;

namespace Neruna.Desktop.Controls;

/// <summary>
/// An/Cc field: addresses separated by commas, with suggestions from the address books for the address being typed
/// (↑/↓ to choose, Enter or Tab to take it, Esc to close).
/// </summary>
internal sealed class RecipientBox : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<RecipientBox, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<RecipientDirectory?> DirectoryProperty =
        AvaloniaProperty.Register<RecipientBox, RecipientDirectory?>(nameof(Directory));

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<RecipientBox, string?>(nameof(PlaceholderText));

    private const int MaxSuggestions = 8;
    private readonly TextBox _box;
    private readonly ListBox _list;
    private readonly Popup _popup;
    private bool _syncing;
    private int _queryVersion;

    public RecipientBox()
    {
        _box = new TextBox();
        _box.Bind(TextBox.PlaceholderTextProperty, this.GetObservable(PlaceholderTextProperty));
        _list = new ListBox
        {
            MaxHeight = 320,
            Focusable = false,
            // The template is also asked for an empty row while a recycled container is cleared (new suggestions).
            ItemTemplate = new FuncDataTemplate<RecipientEntry?>((entry, _) => SuggestionRow(entry)),
        };
        _popup = new Popup
        {
            PlacementTarget = _box,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(2),
                BoxShadow = BoxShadows.Parse("0 4 14 0 #33000000"),
                Child = _list,
            },
        };
        var border = (Border)_popup.Child;
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("PaneBackgroundBrush"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("DividerBrush"));
        Content = new Panel { Children = { _box, _popup } };

        _box.TextChanged += async (_, _) =>
        {
            // Suggestions are a convenience: whatever goes wrong there must never take the window down.
            try
            {
                await OnTypedAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _popup.IsOpen = false;
                System.Diagnostics.Trace.TraceWarning($"Recipient suggestions failed: {ex}");
            }
        };
        _box.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _box.LostFocus += (_, _) => Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (!_box.IsFocused)
            {
                _popup.IsOpen = false;
            }
        }, TimeSpan.FromMilliseconds(200));
        _list.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is RecipientEntry entry)
            {
                e.Handled = true;
                Accept(entry);
            }
        }, RoutingStrategies.Tunnel);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public RecipientDirectory? Directory
    {
        get => GetValue(DirectoryProperty);
        set => SetValue(DirectoryProperty, value);
    }

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>The suggestion list is open (for the snapshot tool).</summary>
    public bool IsSuggesting => _popup.IsOpen;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && !_syncing && _box.Text != Text)
        {
            _syncing = true;
            _box.Text = Text;
            _syncing = false;
        }
    }

    /// <summary>Types <paramref name="text"/> as if from the keyboard (snapshot tool).</summary>
    public async Task TypeAsync(string text)
    {
        _box.Focus();
        _box.Text = text;
        _box.CaretIndex = text.Length;
        await OnTypedAsync();
    }

    private async Task OnTypedAsync()
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        Text = _box.Text;
        _syncing = false;

        var version = ++_queryVersion;
        var (_, _, token) = CurrentToken();
        if (!_box.IsFocused || Directory is not { } directory || token.Length == 0)
        {
            _popup.IsOpen = false;
            return;
        }

        var matches = await directory.SuggestAsync(token, _box.Text ?? string.Empty, MaxSuggestions);
        if (version != _queryVersion)
        {
            return;
        }

        _list.ItemsSource = matches;
        _list.SelectedIndex = matches.Count > 0 ? 0 : -1;
        ((Border)_popup.Child!).MinWidth = Math.Max(320, _box.Bounds.Width);
        _popup.IsOpen = matches.Count > 0;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen || _list.ItemCount == 0)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                _list.SelectedIndex = (_list.SelectedIndex + 1) % _list.ItemCount;
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Up:
                _list.SelectedIndex = (_list.SelectedIndex - 1 + _list.ItemCount) % _list.ItemCount;
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter or Key.Tab when e.KeyModifiers == KeyModifiers.None && _list.SelectedItem is RecipientEntry entry:
                Accept(entry);
                e.Handled = true;
                break;
            case Key.Escape:
                _popup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    // The address under the caret: from the separator before it to the separator after it (or the end).
    private (int Start, int End, string Token) CurrentToken()
    {
        var text = _box.Text ?? string.Empty;
        var caret = Math.Clamp(_box.CaretIndex, 0, text.Length);
        var start = text.LastIndexOfAny([',', ';'], Math.Max(0, caret - 1)) + 1;
        if (caret == 0)
        {
            start = 0;
        }

        var next = text.IndexOfAny([',', ';'], caret);
        var end = next < 0 ? text.Length : next;
        return (start, end, text[start..caret].Trim());
    }

    private void Accept(RecipientEntry entry)
    {
        var text = _box.Text ?? string.Empty;
        var (start, end, _) = CurrentToken();
        var before = text[..start].TrimEnd();
        var after = text[end..].TrimStart(',', ';', ' ');
        var inserted = string.Join(", ", entry.FieldTexts) + ", ";
        var prefix = before.Length == 0 ? string.Empty : before + " ";
        _box.Text = prefix + inserted + after;
        _box.CaretIndex = prefix.Length + inserted.Length;
        _popup.IsOpen = false;
        _box.Focus();
    }

    internal static Control SuggestionRow(RecipientEntry? entry) => entry is null ? new Panel() : new StackPanel
    {
        Margin = new Thickness(2, 1),
        Children =
        {
            new TextBlock { Text = entry.Name, FontWeight = FontWeight.SemiBold },
            new TextBlock
            {
                Text = entry.IsGroup ? entry.Detail : entry.Detail is { Length: > 0 } organization ? $"{entry.Address} · {organization}" : entry.Address,
                FontSize = 12,
                Opacity = 0.75,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        },
    };
}
