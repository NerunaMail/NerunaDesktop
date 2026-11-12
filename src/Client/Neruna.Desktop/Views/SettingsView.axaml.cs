using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Neruna.Desktop.ViewModels;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

internal sealed partial class SettingsView : UserControl
{
    private List<SettingsSearchHit>? _index;
    private Control? _marked;
    private readonly DispatcherTimer _unmark = new() { Interval = TimeSpan.FromSeconds(2.5) };

    public SettingsView()
    {
        InitializeComponent();
        _unmark.Tick += (_, _) => Unmark();
        // Ctrl+F into the search field: MainWindow, for every page ("pageSearch").
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var searching = query.Length > 0;
        NavList.IsVisible = !searching;
        ResultsPanel.IsVisible = searching;
        if (!searching)
        {
            Results.ItemsSource = null;
            return;
        }

        var hits = Search(_index ??= BuildIndex(), query);
        Results.ItemsSource = hits;
        NoResults.IsVisible = hits.Count == 0;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when !string.IsNullOrEmpty(SearchBox.Text):
                SearchBox.Text = string.Empty;
                e.Handled = true;
                break;
            case Key.Enter when Results.ItemsSource is IReadOnlyList<SettingsSearchHit> { Count: > 0 } hits:
                Open(hits[0]);
                e.Handled = true;
                break;
            case Key.Down when Results.ItemCount > 0:
                Results.SelectedIndex = -1;
                Results.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnResultChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is SettingsSearchHit hit)
        {
            Open(hit);
        }
    }

    // The page of the hit, then (once it is laid out) scrolled to the place and marked for a moment.
    private void Open(SettingsSearchHit hit)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.SelectedNav = SettingsViewModel.Navigation.FirstOrDefault(n => !n.IsHeading && n.Section == hit.Section);
        if (hit.Target is not { } target)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Unmark();
            target.BringIntoView();
            target.Classes.Add("searchHit");
            _marked = target;
            _unmark.Start();
        }, DispatcherPriority.Background);
    }

    private void Unmark()
    {
        _unmark.Stop();
        _marked?.Classes.Remove("searchHit");
        _marked = null;
    }

    private static List<SettingsSearchHit> Search(List<SettingsSearchHit> index, string query)
    {
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = index.Where(h => words.All(w => compare.IndexOf(h.Text, w, options) >= 0 || (h.Keywords is { } k && compare.IndexOf(k, w, options) >= 0)))
            .ToList();
        // A page found by one of its other words only counts when nothing on it matched as written. Pages first,
        // then short labels (headings, check boxes), then help texts.
        bool Named(SettingsSearchHit h) => words.All(w => compare.IndexOf(h.Text, w, options) >= 0);
        var pagesWithText = hits.Where(Named).Select(h => h.Section).ToHashSet();
        return hits.Where(h => Named(h) || !pagesWithText.Contains(h.Section))
            .GroupBy(h => h.Text + "\n" + h.Section).Select(g => g.First())
            .OrderBy(h => h.Target is null ? 0 : h.Text.Length <= 60 ? 1 : 2)
            .Take(40).ToList();
    }

    // The texts of every page, in the language shown: headings, labels, check boxes, buttons. Pages not opened yet
    // get their views created here (as opening them would). Plus a few other words people search a page by.
    private List<SettingsSearchHit> BuildIndex()
    {
        var titles = SettingsViewModel.Navigation.Where(n => !n.IsHeading).ToDictionary(n => n.Section, n => n.Title);
        var hits = new List<SettingsSearchHit>();
        foreach (var page in Pages.Children)
        {
            if (page.Tag is not string tag || !Enum.TryParse<SettingsSection>(tag, out var section) || !titles.TryGetValue(section, out var title))
            {
                continue;
            }

            hits.Add(new SettingsSearchHit(section, title, title, null, Keywords(section)));
            Collect(page, section, title, hits);
        }

        return hits;
    }

    private static void Collect(ILogical element, SettingsSection section, string title, List<SettingsSearchHit> hits)
    {
        if (element is ContentControl { Presenter: null } content)
        {
            content.ApplyTemplate();
            content.Presenter?.UpdateChild();
        }

        if (element is ContentControl { Presenter: { Child: null } presenter })
        {
            presenter.UpdateChild();
        }

        switch (element)
        {
            case TextBlock { Text: { Length: > 2 } text } block when block is not SelectableTextBlock:
                hits.Add(new SettingsSearchHit(section, title, OneLine(text), block, null));
                break;
            case ContentControl { Content: string { Length: > 2 } text } control:
                hits.Add(new SettingsSearchHit(section, title, OneLine(text), control, null));
                break;
            case HeaderedContentControl { Header: string { Length: > 2 } header } headered:
                hits.Add(new SettingsSearchHit(section, title, OneLine(header), headered, null));
                break;
        }

        // Lists of the user's own things (accounts, signatures, templates …) are not settings to find
        if (element is ItemsControl)
        {
            return;
        }

        foreach (var child in element.GetLogicalChildren())
        {
            Collect(child, section, title, hits);
        }

        if (element is ContentControl { Presenter.Child: { } shown } && !element.GetLogicalChildren().Contains(shown))
        {
            Collect(shown, section, title, hits);
        }
    }

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? Keywords(SettingsSection section) => section switch
    {
        SettingsSection.Accounts => T("Passwort Anmeldung Server Postfach Adresse Abwesenheit"),
        SettingsSection.Cloud => T("Lizenz Organisation Abo Firma"),
        SettingsSection.Mail => T("Abruf Push Benachrichtigung Lesebestätigung Unterhaltungen"),
        SettingsSection.Compose => T("Editor Fenster Antworten Zitat Rechtschreibung"),
        SettingsSection.Signatures => T("Unterschrift Fusszeile"),
        SettingsSection.TextTemplates => T("Bausteine Vorlagen"),
        SettingsSection.MailTags => T("Kategorien Etiketten Labels"),
        SettingsSection.QuickActions => T("Regeln Verschieben Tastenkürzel"),
        SettingsSection.ServerRules => T("Filter Sieve Weiterleitung"),
        SettingsSection.Calendar => T("Geburtstage Feiertage Abonnement Erinnerung Arbeitszeit"),
        SettingsSection.Meetings => T("Videokonferenz Online-Besprechung"),
        SettingsSection.Appearance => T("Dunkel Hell Design Farbe Schrift Sprache"),
        SettingsSection.Privacy => T("Externe Bilder Tracking Absturzberichte"),
        SettingsSection.Certificates => T("S/MIME Verschlüsselung Signieren Schlüssel"),
        SettingsSection.Pdf => T("Passwort Schutz Anhang"),
        SettingsSection.Data => T("Backup Sicherung Suchindex Speicher Logs Löschen"),
        SettingsSection.Workspaces => T("Profil Arbeitsbereich"),
        SettingsSection.Ai => T("Künstliche Intelligenz Assistent"),
        SettingsSection.Integration => T("Standardprogramm Mailto Senden an"),
        SettingsSection.About => T("Version Update Lizenz"),
        _ => null,
    };
}

/// <summary>A search hit in the settings: a text on a page (or the page itself, found by one of its other words).</summary>
internal sealed record SettingsSearchHit(SettingsSection Section, string Page, string Text, Control? Target, string? Keywords);
