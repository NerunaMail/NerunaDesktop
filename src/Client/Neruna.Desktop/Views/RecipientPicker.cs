using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Neruna.Core.Contacts;
using Neruna.Desktop.Controls;
using Neruna.Desktop.Infrastructure;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

/// <summary>Choosing one or more contacts or groups from the address books, for An or Cc.</summary>
internal static class RecipientPicker
{
    /// <summary>The picker on screen, if any (the snapshot tool photographs it).</summary>
    public static Window? Open { get; private set; }

    /// <returns>The chosen entries; empty when cancelled.</returns>
    public static async Task<IReadOnlyList<RecipientEntry>> ShowAsync(Window owner, string field, RecipientDirectory directory, string? preset = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(directory);
        var all = await directory.GetAllAsync();
        var chosen = new List<RecipientEntry>();
        var confirmed = false;

        var dialog = new Window
        {
            Title = F("Kontakte auswählen – {0}", field),
            Width = 520,
            Height = 600,
            MinWidth = 400,
            MinHeight = 360,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = owner.Icon,
        };
        dialog.Bind(Window.BackgroundProperty, dialog.GetResourceObservable("PaneBackgroundBrush"));

        var search = new TextBox { PlaceholderText = T("Name, Firma oder Adresse suchen"), Text = preset };
        var list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle,
            ItemTemplate = new FuncDataTemplate<RecipientEntry?>((_, _) => Row()),
        };
        var empty = new TextBlock
        {
            Text = all.Count == 0 ? T("In den Adressbüchern gibt es noch keine Kontakte mit E-Mail-Adresse.") : T("Keine Treffer."),
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 8),
        };
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        var take = new Button { Content = F("Zu «{0}» hinzufügen", field), IsDefault = true, IsEnabled = false };
        take.Classes.Add("accent");
        var cancel = new Button { Content = T("Abbrechen"), IsCancel = true };

        var refilling = false;
        void Refill()
        {
            refilling = true;
            var items = RecipientEntry.Find(all, search.Text ?? string.Empty);
            list.ItemsSource = items;
            foreach (var entry in chosen.Where(items.Contains))
            {
                list.SelectedItems!.Add(entry);
            }

            empty.IsVisible = items.Count == 0;
            refilling = false;
        }

        void ShowCount()
        {
            count.Text = chosen.Count switch
            {
                0 => T("Kontakte anklicken, um sie auszuwählen."),
                1 => T("1 ausgewählt"),
                var n => F("{0} ausgewählt", n),
            };
            take.IsEnabled = chosen.Count > 0;
        }

        list.SelectionChanged += (_, e) =>
        {
            if (refilling)
            {
                return;
            }

            chosen.RemoveAll(entry => e.RemovedItems.Contains(entry));
            chosen.AddRange(e.AddedItems.OfType<RecipientEntry>().Where(entry => !chosen.Contains(entry)));
            ShowCount();
        };
        list.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is RecipientEntry entry)
            {
                if (!chosen.Contains(entry))
                {
                    chosen.Add(entry);
                }

                confirmed = true;
                dialog.Close();
            }
        };
        search.TextChanged += (_, _) => Refill();
        search.KeyDown += (_, e) =>
        {
            // ↓ from the search moves into the list; Space there toggles the contact.
            if (e.Key == Key.Down && list.ItemCount > 0)
            {
                list.Focus();
                list.SelectedIndex = list.SelectedIndex < 0 ? 0 : list.SelectedIndex;
                e.Handled = true;
            }
        };
        take.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        footer.Children.Add(count);
        Grid.SetColumn(take, 1);
        Grid.SetColumn(cancel, 2);
        footer.Children.Add(take);
        footer.Children.Add(cancel);

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 10, Margin = new Thickness(20, 16) };
        var listArea = new Panel { Children = { list, empty } };
        Grid.SetRow(listArea, 1);
        Grid.SetRow(footer, 2);
        body.Children.Add(search);
        body.Children.Add(listArea);
        body.Children.Add(footer);
        dialog.Content = body;

        Refill();
        ShowCount();
        dialog.Opened += (_, _) => search.Focus();

        Open = dialog;
        try
        {
            await dialog.ShowDialog(owner);
        }
        finally
        {
            Open = null;
        }

        return confirmed ? chosen : [];
    }

    // Checkbox (follows the selection) + name and address/organization.
    private static Grid Row()
    {
        var check = new CheckBox { IsHitTestVisible = false, Focusable = false, MinWidth = 0, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ListBoxItem.IsSelected))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(ListBoxItem) },
        });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(check);
        var text = new ContentControl();
        text.Bind(ContentControl.ContentProperty, new Binding("."));
        text.ContentTemplate = new FuncDataTemplate<RecipientEntry?>((entry, _) => RecipientBox.SuggestionRow(entry));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }
}
