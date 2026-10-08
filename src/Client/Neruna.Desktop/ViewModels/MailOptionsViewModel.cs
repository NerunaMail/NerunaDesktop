using System.Globalization;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core;
using Neruna.Desktop.Editor;

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
            new(MarkAsReadMode.OnSelect, "Beim Anklicken (sofort)", Select),
            new(MarkAsReadMode.AfterDelay, "Nach 10 Sekunden im Lesebereich", Select),
            new(MarkAsReadMode.OnReply, "Erst beim Antworten oder Weiterleiten", Select),
            new(MarkAsReadMode.Never, "Nie automatisch (nur manuell)", Select),
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
    public partial double ComposeFontSize { get; set; } = 11;

    public MarkAsReadMode MarkAsRead => MarkAsReadOptions.FirstOrDefault(o => o.IsSelected)?.Mode ?? MarkAsReadMode.OnSelect;

    public async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            ComposeInWindow = await _settings.GetBoolAsync(SettingKeys.ComposeInWindow);
            Push = await _settings.GetBoolAsync(SettingKeys.MailPush, fallback: true);
            Notifications = await _settings.GetBoolAsync(SettingKeys.MailNotifications, fallback: true);
            ComposeFont = FontCatalog.Find(await _settings.GetAsync(SettingKeys.ComposeFont)) ?? FontCatalog.Find(FontCatalog.DefaultFont);
            ComposeFontSize = double.TryParse(await _settings.GetAsync(SettingKeys.ComposeFontSize), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? size : 11;
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
