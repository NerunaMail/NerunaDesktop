using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Neruna.Core.Contacts;
using Neruna.Desktop.Infrastructure;
using MimeKit;

namespace Neruna.Desktop.Controls;

/// <summary>
/// An/Cc/Bcc field: each complete address becomes a box ("Kästchen") showing the name – from the input
/// (<c>Anna Muster &lt;anna@example.com&gt;</c>) or the address book – or else the address; × removes it, a double
/// click puts it back into the text to edit, Backspace in the empty input marks the last box and a second Backspace
/// removes it. While typing, the address books suggest entries (↑/↓ to choose, Enter or Tab to take it, Esc to close).
/// <see cref="Text"/> stays the plain comma-separated list (what the view model reads and writes).
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
    private const double InputMinWidth = 140;
    private readonly Border _frame;
    private readonly WrapPanel _panel;
    private readonly TextBox _box;
    private readonly ListBox _list;
    private readonly Popup _popup;
    private readonly List<Chip> _chips = [];
    private Chip? _marked;
    private bool _syncing;
    private int _queryVersion;

    /// <summary>One recipient as a box: the text as it goes into the field, and the control showing it.</summary>
    private sealed class Chip(string text, string address, Border view, TextBlock label)
    {
        public string Text { get; } = text;
        public string Address { get; } = address;
        public Border View { get; } = view;
        public TextBlock Label { get; } = label;
    }

    /// <summary>Puts the caret into the field (at the end of what is there).</summary>
    public void FocusText()
    {
        _box.Focus();
        _box.CaretIndex = _box.Text?.Length ?? 0;
    }

    public RecipientBox()
    {
        _box = new TextBox { MinWidth = InputMinWidth, Classes = { "recipientInput" }, VerticalAlignment = VerticalAlignment.Center };
        _panel = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 3, Children = { _box } };
        _frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 2),
            MinHeight = 32,
            Cursor = new Cursor(StandardCursorType.Ibeam),
            Child = _panel,
        };
        _frame.Bind(Border.BackgroundProperty, _frame.GetResourceObservable("PaneBackgroundBrush"));
        UpdateFrameBrush();

        // The inner TextBox draws no frame of its own: the whole field (boxes and input) has one.
        Styles.Add(new Style(x => x.OfType<TextBox>().Class("recipientInput"))
        {
            Setters =
            {
                new Setter(TextBox.BorderThicknessProperty, new Thickness(0)),
                new Setter(TextBox.BackgroundProperty, Brushes.Transparent),
                new Setter(TextBox.MinHeightProperty, 26.0),
                new Setter(TextBox.PaddingProperty, new Thickness(2, 3)),
            },
        });
        foreach (var state in new[] { ":pointerover", ":focus", ":focus-within", "" })
        {
            Styles.Add(new Style(x => (state.Length == 0 ? x.OfType<TextBox>().Class("recipientInput") : x.OfType<TextBox>().Class("recipientInput").Class(state))
                .Template().OfType<Border>().Name("PART_BorderElement"))
            {
                Setters =
                {
                    new Setter(Border.BorderThicknessProperty, new Thickness(0)),
                    new Setter(Border.BackgroundProperty, Brushes.Transparent),
                },
            });
        }

        _list = new ListBox
        {
            MaxHeight = 320,
            Focusable = false,
            // The template is also asked for an empty row while a recycled container is cleared (new suggestions).
            ItemTemplate = new FuncDataTemplate<RecipientEntry?>((entry, _) => SuggestionRow(entry)),
        };
        _popup = new Popup
        {
            PlacementTarget = _frame,
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
        Content = new Panel { Children = { _frame, _popup } };

        // A click into the field (beside the boxes) puts the caret into the input.
        _frame.PointerPressed += (_, e) =>
        {
            if (e.Source == _frame || e.Source == _panel)
            {
                FocusText();
                e.Handled = true;
            }
        };
        _frame.SizeChanged += (_, _) => FitInput();
        _panel.LayoutUpdated += (_, _) => FitInputLater();
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
        _box.GotFocus += (_, _) => UpdateFrameBrush();
        _box.LostFocus += (_, _) =>
        {
            Mark(null);
            UpdateFrameBrush();
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                if (!_box.IsFocused)
                {
                    _popup.IsOpen = false;
                    CommitTyped(all: true);
                }
            }, TimeSpan.FromMilliseconds(200));
        };
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

    /// <summary>What the boxes show, in order (for the snapshot tool).</summary>
    public IReadOnlyList<string> ChipLabels => [.. _chips.Select(c => c.Label.Text ?? string.Empty)];

    /// <summary>The text still being typed (not yet a box).</summary>
    public string TypedText => _box.Text ?? string.Empty;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && !_syncing && Combined() != (Text ?? string.Empty))
        {
            Rebuild(Text ?? string.Empty);
        }
        else if (change.Property == PlaceholderTextProperty)
        {
            FitInput();
        }
        else if (change.Property == DirectoryProperty)
        {
            foreach (var chip in _chips)
            {
                _ = NameFromAddressBookAsync(chip);
            }
        }
    }

    /// <summary>Types <paramref name="text"/> as if from the keyboard into an empty field (snapshot tool).</summary>
    public async Task TypeAsync(string text)
    {
        _box.Focus();
        _syncing = true;
        foreach (var chip in _chips.ToList())
        {
            RemoveChip(chip, publish: false);
        }

        _box.Text = string.Empty;
        _syncing = false;
        _box.Text = text;
        _box.CaretIndex = _box.Text?.Length ?? 0;
        await OnTypedAsync();
    }

    /// <summary>Presses a key in the input (snapshot tool).</summary>
    public void Press(Key key) => OnKeyDown(_box, new KeyEventArgs { Key = key, RoutedEvent = KeyDownEvent });

    // ---- The boxes ----------------------------------------------------------------------------------------------

    private string Combined()
    {
        var typed = (_box.Text ?? string.Empty).Trim().TrimEnd(',', ';').Trim();
        return string.Join(", ", _chips.Select(c => c.Text).Append(typed).Where(t => t.Length > 0));
    }

    private void Publish()
    {
        _syncing = true;
        Text = Combined();
        _syncing = false;
        FitInput();
    }

    // Text set from outside (reply, picker, draft): complete addresses become boxes, anything else stays text.
    private void Rebuild(string text)
    {
        _syncing = true;
        foreach (var chip in _chips.ToList())
        {
            RemoveChip(chip, publish: false);
        }

        var (addresses, rest) = RecipientField.Split(text, all: true);
        foreach (var (raw, mailbox) in addresses)
        {
            AddChip(raw, mailbox);
        }

        _box.Text = rest;
        _syncing = false;
        FitInput();
    }

    private static string NameOf(MailboxAddress mailbox) => (mailbox.Name ?? string.Empty).Trim().Trim('\'', '"').Trim();

    private void AddChip(string raw, MailboxAddress mailbox)
    {
        var name = NameOf(mailbox);
        var label = new TextBlock
        {
            Text = name.Length > 0 ? name : mailbox.Address,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 260,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var remove = new Button
        {
            Content = new PathIcon { Data = Application.Current?.TryFindResource("IconClose", out var icon) == true ? (Geometry)icon! : null, Width = 10, Height = 10 },
            Padding = new Thickness(4),
            MinWidth = 0,
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        ToolTip.SetTip(remove, Neruna.Core.Localization.Texts.T("Empfänger entfernen"));
        var view = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 1, 1, 1),
            Cursor = new Cursor(StandardCursorType.Arrow),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { label, remove } },
        };
        view.Bind(Border.BackgroundProperty, view.GetResourceObservable("TodayBrush"));
        view.Bind(Border.BorderBrushProperty, view.GetResourceObservable("TodayBrush"));
        ToolTip.SetTip(view, name.Length > 0 ? $"{name} <{mailbox.Address}>" : mailbox.Address);

        var chip = new Chip(raw, mailbox.Address, view, label);
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            RemoveChip(chip, publish: true);
            FocusText();
        };
        view.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            Edit(chip);
        };
        view.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Mark(chip);
            _box.Focus();
        };

        _chips.Add(chip);
        _panel.Children.Insert(_panel.Children.IndexOf(_box), view);
        if (name.Length == 0)
        {
            _ = NameFromAddressBookAsync(chip);
        }
    }

    // Only an address typed: the name from the address book, if one has it.
    private async Task NameFromAddressBookAsync(Chip chip)
    {
        if (Directory is not { } directory || chip.Label.Text != chip.Address)
        {
            return;
        }

        try
        {
            var entry = (await directory.GetAllAsync()).FirstOrDefault(e => !e.IsGroup && e.Name.Length > 0
                && string.Equals(e.Address, chip.Address, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(e.Name, chip.Address, StringComparison.OrdinalIgnoreCase));
            if (entry is not null && _chips.Contains(chip))
            {
                chip.Label.Text = entry.Name;
                ToolTip.SetTip(chip.View, $"{entry.Name} <{chip.Address}>");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning($"Recipient name lookup failed: {ex.Message}");
        }
    }

    private void RemoveChip(Chip chip, bool publish)
    {
        if (_marked == chip)
        {
            _marked = null;
        }

        _chips.Remove(chip);
        _panel.Children.Remove(chip.View);
        if (publish)
        {
            Publish();
        }
    }

    // Double click: the box goes back into the input as text (what was typed there is kept as a box if complete).
    private void Edit(Chip chip)
    {
        CommitTyped(all: true);
        RemoveChip(chip, publish: false);
        _syncing = true;
        _box.Text = chip.Text;
        _syncing = false;
        Publish();
        FocusText();
    }

    private void Mark(Chip? chip)
    {
        if (_marked is { } old)
        {
            old.View.Bind(Border.BorderBrushProperty, old.View.GetResourceObservable("TodayBrush"));
        }

        _marked = chip;
        if (chip is not null)
        {
            chip.View.Bind(Border.BorderBrushProperty, chip.View.GetResourceObservable("AccentTextBrush"));
        }
    }

    // Complete addresses typed so far become boxes (with all: also the last one, e.g. on leaving the field).
    private void CommitTyped(bool all)
    {
        var typed = _box.Text ?? string.Empty;
        var (addresses, rest) = RecipientField.Split(typed, all);
        if (addresses.Count == 0)
        {
            return;
        }

        foreach (var (raw, mailbox) in addresses)
        {
            AddChip(raw, mailbox);
        }

        _syncing = true;
        _box.Text = rest;
        _box.CaretIndex = rest.Length;
        _syncing = false;
        Publish();
    }

    private bool _fitPending;

    private void FitInputLater()
    {
        if (_fitPending)
        {
            return;
        }

        _fitPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _fitPending = false;
            FitInput();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    // The input takes the rest of the line; with no boxes the whole width (room for the hint text).
    private void FitInput()
    {
        // The hint ("Name oder Adresse eingeben …") only in an empty field.
        _box.PlaceholderText = _chips.Count == 0 ? PlaceholderText : null;

        var width = _frame.Bounds.Width - _frame.Padding.Left - _frame.Padding.Right - 2;
        if (width <= 0)
        {
            return;
        }

        // After the boxes are laid out: right of the last one, or a line of its own when too little is left there.
        var rest = _chips.Count == 0 ? width : width - _chips[^1].View.Bounds.Right - _panel.ItemSpacing;
        var fitted = rest >= InputMinWidth ? Math.Floor(rest) : width;
        if (Math.Abs(fitted - _box.Width) > 0.5 || double.IsNaN(_box.Width))
        {
            _box.Width = fitted;
        }
    }

    // ---- Typing and suggestions --------------------------------------------------------------------------------

    private async Task OnTypedAsync()
    {
        if (_syncing)
        {
            return;
        }

        Mark(null);
        if ((_box.Text ?? string.Empty).IndexOfAny([',', ';']) >= 0)
        {
            CommitTyped(all: false);
        }

        Publish();

        var version = ++_queryVersion;
        var (_, _, token) = CurrentToken();
        if (!_box.IsFocused || Directory is not { } directory || token.Length == 0)
        {
            _popup.IsOpen = false;
            return;
        }

        var matches = await directory.SuggestAsync(token, Text ?? string.Empty, MaxSuggestions);
        if (version != _queryVersion)
        {
            return;
        }

        _list.ItemsSource = matches;
        _list.SelectedIndex = matches.Count > 0 ? 0 : -1;
        ((Border)_popup.Child!).MinWidth = Math.Max(320, _frame.Bounds.Width);
        _popup.IsOpen = matches.Count > 0;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_popup.IsOpen && _list.ItemCount > 0)
        {
            switch (e.Key)
            {
                case Key.Down:
                    _list.SelectedIndex = (_list.SelectedIndex + 1) % _list.ItemCount;
                    _list.ScrollIntoView(_list.SelectedIndex);
                    e.Handled = true;
                    return;
                case Key.Up:
                    _list.SelectedIndex = (_list.SelectedIndex - 1 + _list.ItemCount) % _list.ItemCount;
                    _list.ScrollIntoView(_list.SelectedIndex);
                    e.Handled = true;
                    return;
                case Key.Enter or Key.Tab when e.KeyModifiers == KeyModifiers.None && _list.SelectedItem is RecipientEntry entry:
                    Accept(entry);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    _popup.IsOpen = false;
                    e.Handled = true;
                    return;
            }
        }

        var atStart = _box.CaretIndex == 0 && _box.SelectionStart == _box.SelectionEnd;
        switch (e.Key)
        {
            // Backspace at the start: first marks the box before, then removes it.
            case Key.Back when atStart && e.KeyModifiers == KeyModifiers.None && _chips.Count > 0:
                if (_marked is { } marked)
                {
                    RemoveChip(marked, publish: true);
                }
                else
                {
                    Mark(_chips[^1]);
                }

                e.Handled = true;
                return;
            case Key.Delete when _marked is { } markedChip:
                RemoveChip(markedChip, publish: true);
                e.Handled = true;
                return;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None && (_box.Text ?? string.Empty).Trim().Length > 0:
                CommitTyped(all: true);
                e.Handled = true;
                return;
            case Key.Tab:
                CommitTyped(all: true);
                break;
        }

        if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl))
        {
            Mark(null);
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
        var rest = (text[..start].TrimEnd() + " " + text[end..].TrimStart(',', ';', ' ')).Trim();
        foreach (var field in entry.FieldTexts)
        {
            if (RecipientField.AsMailbox(field) is { } mailbox && !_chips.Any(c => string.Equals(c.Address, mailbox.Address, StringComparison.OrdinalIgnoreCase)))
            {
                AddChip(field, mailbox);
            }
        }

        _syncing = true;
        _box.Text = rest;
        _box.CaretIndex = rest.Length;
        _syncing = false;
        _popup.IsOpen = false;
        Publish();
        _box.Focus();
    }

    private void UpdateFrameBrush() =>
        _frame.Bind(Border.BorderBrushProperty, _frame.GetResourceObservable(_box.IsFocused ? "AccentTextBrush" : "DividerBrush"));

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
