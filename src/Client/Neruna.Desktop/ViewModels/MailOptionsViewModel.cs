using System.Globalization;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core;
using Neruna.Desktop.Editor;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>Settings → E-Mail: how mails are written and when they count as read. Changes apply immediately.</summary>
internal sealed partial class MailOptionsViewModel : ViewModelBase
{
    private readonly ISettingsStore _settings;
    private bool _loading;

    public MailOptionsViewModel(ISettingsStore settings)
    {
        _settings = settings;
        MarkAsReadOptions =
        [
            new(MarkAsReadMode.OnSelect, T("Beim Anklicken (sofort)"), Select),
            new(MarkAsReadMode.AfterDelay, T("Nach 10 Sekunden im Lesebereich"), Select),
            new(MarkAsReadMode.OnReply, T("Erst beim Antworten oder Weiterleiten"), Select),
            new(MarkAsReadMode.Never, T("Nie automatisch (nur manuell)"), Select),
        ];
    }

    /// <summary>Preview at real size: 1pt = 4/3 px.</summary>
    public static FuncValueConverter<double, double> PointsToPixels { get; } = new(pt => pt * 4 / 3);

    public IReadOnlyList<MarkAsReadOption> MarkAsReadOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposeInline))]
    public partial bool ComposeInWindow { get; set; }

    public bool ComposeInline
    {
        get => !ComposeInWindow;
        set => ComposeInWindow = !value;
    }

    /// <summary>"Test-Benachrichtigung anzeigen": the shell shows one, to check it works on this computer.</summary>
    public event EventHandler? TestNotificationRequested;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void TestNotification() => TestNotificationRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The push setting changed: the shell starts or stops watching the inboxes.</summary>
    public event EventHandler? PushChanged;

    [ObservableProperty]
    public partial bool Push { get; set; } = true;

    [ObservableProperty]
    public partial bool Notifications { get; set; } = true;

    [ObservableProperty]
    public partial FontChoice? ComposeFont { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ParagraphSpacingPixels))]
    public partial double ComposeFontSize { get; set; } = 11;

    /// <summary>Einstellungen → Schreiben: the space after a paragraph (Enter), in lines.</summary>
    public static IReadOnlyList<ParagraphSpacingChoice> ParagraphSpacingChoices { get; } =
    [
        new(0, T("Kein Abstand")),
        new(0.25, T("Klein (¼ Zeile)")),
        new(0.5, T("Mittel (½ Zeile)")),
        new(0.75, T("Gross (¾ Zeile)")),
        new(1, T("Eine ganze Zeile")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ParagraphSpacingPixels))]
    public partial ParagraphSpacingChoice ParagraphSpacing { get; set; } = ParagraphSpacingChoices[2];

    /// <summary>The preview's space between its two paragraphs.</summary>
    public double ParagraphSpacingPixels => ParagraphSpacing.Lines * ComposeFontSize * 4 / 3;

    public MarkAsReadMode MarkAsRead => MarkAsReadOptions.FirstOrDefault(o => o.IsSelected)?.Mode ?? MarkAsReadMode.OnSelect;

    /// <summary>Only where Neruna knows how to register (Windows, macOS, Linux).</summary>
    public static bool CanBeDefaultApp => Infrastructure.DefaultMailApp.Current is not null;

    [ObservableProperty]
    public partial bool IsDefaultApp { get; set; }

    [ObservableProperty]
    public partial string DefaultAppStatus { get; set; } = T("Wird geladen …");

    /// <summary>After the button on Windows: what to do in the system settings.</summary>
    [ObservableProperty]
    public partial string? DefaultAppHint { get; set; }

    /// <summary>Asks the system (xdg-mime on Linux is a process, so off the UI thread).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public async Task CheckDefaultAppAsync()
    {
        if (Infrastructure.DefaultMailApp.Current is not { } app)
        {
            return;
        }

        var status = await Task.Run(app.GetStatus);
        IsDefaultApp = status.IsDefault;
        DefaultAppStatus = status switch
        {
            { IsDefault: true } => T("Neruna ist das Standard-Mailprogramm: E-Mail-Links (mailto:) und .eml-Dateien öffnen sich in Neruna."),
            { Mailto: true } => T("E-Mail-Links (mailto:) öffnen sich in Neruna, .eml-Dateien noch in einem anderen Programm."),
            { Eml: true } => T(".eml-Dateien öffnen sich in Neruna, E-Mail-Links (mailto:) noch in einem anderen Programm."),
            _ => T("Neruna ist nicht das Standard-Mailprogramm. E-Mail-Links (mailto:) und .eml-Dateien öffnen sich in einem anderen Programm."),
        };
        if (status.IsDefault)
        {
            DefaultAppHint = null;
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task MakeDefaultAppAsync()
    {
        if (Infrastructure.DefaultMailApp.Current is not { } app)
        {
            return;
        }

        try
        {
            var userMustChoose = await Task.Run(app.MakeDefault);
            DefaultAppHint = userMustChoose
                ? T("Die Windows-Einstellungen sind geöffnet: Wählen Sie bei «MAILTO» und «.eml» Neruna aus und klicken Sie danach auf «Erneut prüfen».")
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            DefaultAppHint = F("Neruna konnte sich nicht registrieren: {0}", ex.Message);
        }

        await CheckDefaultAppAsync();
    }

    public async Task ReloadAsync()
    {
        _ = CheckDefaultAppAsync();
        _loading = true;
        try
        {
            ComposeInWindow = await _settings.GetBoolAsync(SettingKeys.ComposeInWindow);
            AutoLists = await _settings.GetBoolAsync(SettingKeys.ComposeAutoLists, fallback: true);
            SpellLanguage = SpellLanguages.Find(await _settings.GetAsync(SettingKeys.ComposeSpellLanguage));
            var pdfAttachments = await _settings.GetAsync(SettingKeys.MailPdfAttachments);
            PdfAttachments = pdfAttachments is "beside" or "none" ? pdfAttachments : "inside";
            FormatPlainText = await _settings.GetBoolAsync(SettingKeys.ComposePlainText);
            Push = await _settings.GetBoolAsync(SettingKeys.MailPush, fallback: true);
            Notifications = await _settings.GetBoolAsync(SettingKeys.MailNotifications, fallback: true);
            ComposeFont = FontCatalog.Find(await _settings.GetAsync(SettingKeys.ComposeFont)) ?? FontCatalog.Find(FontCatalog.DefaultFont);
            ComposeFontSize = double.TryParse(await _settings.GetAsync(SettingKeys.ComposeFontSize), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? size : 11;
            var spacing = await _settings.GetParagraphSpacingAsync();
            ParagraphSpacing = ParagraphSpacingChoices.MinBy(c => Math.Abs(c.Lines - spacing))!;
            var mode = Enum.TryParse<MarkAsReadMode>(await _settings.GetAsync(SettingKeys.MarkAsRead), out var parsed) ? parsed : MarkAsReadMode.OnSelect;
            foreach (var option in MarkAsReadOptions)
            {
                option.IsSelected = option.Mode == mode;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnComposeInWindowChanged(bool value) => Save(SettingKeys.ComposeInWindow, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Einstellungen → Schreiben: "- " and "1. " become lists.</summary>
    [ObservableProperty]
    public partial bool AutoLists { get; set; } = true;

    /// <summary>Einstellungen → Schreiben: new mails and answers as plain text (the simple text editor).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatHtml))]
    public partial bool FormatPlainText { get; set; }

    public bool FormatHtml
    {
        get => !FormatPlainText;
        set => FormatPlainText = !value;
    }

    /// <summary>A mail saved as PDF: its pictures and PDFs as pages ("inside"), as files beside it ("beside") or not ("none").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PdfAttachmentsInside), nameof(PdfAttachmentsBeside), nameof(PdfAttachmentsNone))]
    public partial string PdfAttachments { get; set; } = "inside";

    public bool PdfAttachmentsInside
    {
        get => PdfAttachments == "inside";
        set
        {
            if (value)
            {
                PdfAttachments = "inside";
            }
        }
    }

    public bool PdfAttachmentsBeside
    {
        get => PdfAttachments == "beside";
        set
        {
            if (value)
            {
                PdfAttachments = "beside";
            }
        }
    }

    public bool PdfAttachmentsNone
    {
        get => PdfAttachments == "none";
        set
        {
            if (value)
            {
                PdfAttachments = "none";
            }
        }
    }

    partial void OnPdfAttachmentsChanged(string value) => Save(SettingKeys.MailPdfAttachments, value);

    partial void OnAutoListsChanged(bool value) => Save(SettingKeys.ComposeAutoLists, value.ToString(CultureInfo.InvariantCulture));

    public static IReadOnlyList<SpellLanguage> SpellLanguageChoices => SpellLanguages.All;

    public static bool CanChooseSpellLanguage => SpellLanguages.CanChoose;

    /// <summary>Einstellungen → Schreiben: the language new mails are checked in (one can switch while writing).</summary>
    [ObservableProperty]
    public partial SpellLanguage SpellLanguage { get; set; } = SpellLanguages.All[0];

    partial void OnSpellLanguageChanged(SpellLanguage value)
    {
        if (value is null)
        {
            return;
        }

        HtmlEditor.DefaultSpellLanguage = value.Code;
        Save(SettingKeys.ComposeSpellLanguage, value.Code ?? string.Empty);
    }

    partial void OnFormatPlainTextChanged(bool value)
    {
        Editor.HtmlEditor.PlainTextOnly = value; // from the next mail written on
        Save(SettingKeys.ComposePlainText, value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnPushChanged(bool value)
    {
        if (!_loading)
        {
            _ = SaveAndNotifyAsync();
        }

        async Task SaveAndNotifyAsync()
        {
            await _settings.SetAsync(SettingKeys.MailPush, value.ToString(CultureInfo.InvariantCulture));
            PushChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnNotificationsChanged(bool value) => Save(SettingKeys.MailNotifications, value.ToString(CultureInfo.InvariantCulture));

    partial void OnComposeFontChanged(FontChoice? value)
    {
        if (value is { IsFont: true })
        {
            Save(SettingKeys.ComposeFont, value.Name);
        }
    }

    partial void OnComposeFontSizeChanged(double value) => Save(SettingKeys.ComposeFontSize, value.ToString(CultureInfo.InvariantCulture));

    partial void OnParagraphSpacingChanged(ParagraphSpacingChoice value)
    {
        // Reading follows at once (from the next mail opened); writing from the next message.
        Infrastructure.MailPaper.ParagraphLines = value.Lines;
        Save(SettingKeys.ComposeParagraphSpacing, value.Lines.ToString(CultureInfo.InvariantCulture));
    }

    private void Select(MarkAsReadMode mode) => Save(SettingKeys.MarkAsRead, mode.ToString());

    private void Save(string key, string value)
    {
        if (!_loading)
        {
            _ = _settings.SetAsync(key, value);
        }
    }
}

internal sealed partial class MarkAsReadOption(MarkAsReadMode mode, string label, Action<MarkAsReadMode> selected) : ObservableObject
{
    public MarkAsReadMode Mode { get; } = mode;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            selected(Mode);
        }
    }
}

/// <summary>A choice for the space after a paragraph, in lines.</summary>
public sealed record ParagraphSpacingChoice(double Lines, string Name)
{
    public override string ToString() => Name;
}
