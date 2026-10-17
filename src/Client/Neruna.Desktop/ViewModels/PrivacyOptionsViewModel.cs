using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Privacy;
using Neruna.Desktop.Infrastructure;

namespace Neruna.Desktop.ViewModels;

/// <summary>Settings → Datenschutz: what is blocked, what only after asking. Changes apply to the next mail opened.</summary>
internal sealed partial class PrivacyOptionsViewModel : ViewModelBase
{
    private readonly PrivacyService privacy;
    private bool _loading;

    public PrivacyOptionsViewModel(PrivacyService privacy)
    {
        this.privacy = privacy;
        // "Immer laden" above a mail adds a sender: the list here follows.
        privacy.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (!_saving)
            {
                await ReloadAsync();
            }
        });
    }

    private bool _saving;

    [ObservableProperty]
    public partial bool RemoteBlock { get; set; }

    [ObservableProperty]
    public partial bool RemoteAsk { get; set; } = true;

    [ObservableProperty]
    public partial bool RemoteAllow { get; set; }

    [ObservableProperty]
    public partial bool BlockTrackers { get; set; } = true;

    [ObservableProperty]
    public partial bool PlainText { get; set; }

    [ObservableProperty]
    public partial bool ConfirmNever { get; set; }

    [ObservableProperty]
    public partial bool ConfirmSuspicious { get; set; } = true;

    [ObservableProperty]
    public partial bool ConfirmAlways { get; set; }

    [ObservableProperty]
    public partial bool CleanLinks { get; set; } = true;

    [ObservableProperty]
    public partial bool WarnSuspicious { get; set; } = true;

    [ObservableProperty]
    public partial bool HideTimeZone { get; set; }

    /// <summary>"Hinweis zu Spam in der Sicherheitsprüfung anzeigen".</summary>
    [ObservableProperty]
    public partial bool ShowSpamNote { get; set; } = true;

    /// <summary>Senders and domains whose pictures load without asking.</summary>
    public ObservableCollection<string> TrustedSenders { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrustedSenders), nameof(TrustedCountText))]
    public partial int TrustedCount { get; set; }

    public bool HasTrustedSenders => TrustedCount > 0;

    public string TrustedCountText => TrustedCount == 1 ? Neruna.Core.Localization.Texts.T("1 Eintrag") : Neruna.Core.Localization.Texts.F("{0} Einträge", TrustedCount);

    /// <summary>Typed in to add: an address or a domain.</summary>
    [ObservableProperty]
    public partial string NewTrusted { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? TrustedError { get; set; }

    [RelayCommand]
    private async Task AddTrustedAsync()
    {
        if (PrivacyOptions.TrustedEntry(NewTrusted) is not { } entry)
        {
            TrustedError = Neruna.Core.Localization.Texts.T("Bitte eine E-Mail-Adresse (anna@firma.ch) oder eine Domain (@firma.ch) eingeben.");
            return;
        }

        TrustedError = null;
        NewTrusted = string.Empty;
        if (!TrustedSenders.Contains(entry, StringComparer.OrdinalIgnoreCase))
        {
            var index = TrustedSenders.TakeWhile(t => StringComparer.OrdinalIgnoreCase.Compare(t, entry) < 0).Count();
            TrustedSenders.Insert(index, entry);
            TrustedCount = TrustedSenders.Count;
            await SaveAsync();
        }
    }

    public async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            var options = await privacy.GetAsync();
            RemoteBlock = options.RemoteContent == RemoteContentPolicy.Block;
            RemoteAsk = options.RemoteContent == RemoteContentPolicy.Ask;
            RemoteAllow = options.RemoteContent == RemoteContentPolicy.Allow;
            BlockTrackers = options.BlockTrackers;
            PlainText = options.PlainText;
            ConfirmNever = options.ConfirmLinks == LinkConfirmation.Never;
            ConfirmSuspicious = options.ConfirmLinks == LinkConfirmation.Suspicious;
            ConfirmAlways = options.ConfirmLinks == LinkConfirmation.Always;
            CleanLinks = options.CleanLinks;
            WarnSuspicious = options.WarnSuspicious;
            HideTimeZone = options.HideTimeZone;
            ShowSpamNote = !options.HideSpamNote;
            TrustedSenders.Clear();
            foreach (var sender in options.TrustedSenders.Order(StringComparer.OrdinalIgnoreCase))
            {
                TrustedSenders.Add(sender);
            }

            TrustedCount = TrustedSenders.Count;
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private async Task RemoveTrustedAsync(string sender)
    {
        TrustedSenders.Remove(sender);
        TrustedCount = TrustedSenders.Count;
        await SaveAsync();
    }

    // Radio buttons: only the one switched on saves (the others follow).
    partial void OnRemoteBlockChanged(bool value) => SaveIf(value);

    partial void OnRemoteAskChanged(bool value) => SaveIf(value);

    partial void OnRemoteAllowChanged(bool value) => SaveIf(value);

    partial void OnConfirmNeverChanged(bool value) => SaveIf(value);

    partial void OnConfirmSuspiciousChanged(bool value) => SaveIf(value);

    partial void OnConfirmAlwaysChanged(bool value) => SaveIf(value);

    partial void OnBlockTrackersChanged(bool value) => SaveIf(true);

    partial void OnPlainTextChanged(bool value) => SaveIf(true);

    partial void OnCleanLinksChanged(bool value) => SaveIf(true);

    partial void OnWarnSuspiciousChanged(bool value) => SaveIf(true);

    partial void OnHideTimeZoneChanged(bool value) => SaveIf(true);

    partial void OnShowSpamNoteChanged(bool value) => SaveIf(true);

    private void SaveIf(bool changed)
    {
        if (changed && !_loading)
        {
            _ = SaveAsync();
        }
    }

    private async Task SaveAsync()
    {
        _saving = true;
        try
        {
            await SaveCoreAsync();
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task SaveCoreAsync()
    {
        var options = await privacy.GetAsync();
        await privacy.SetAsync(options with
        {
            RemoteContent = RemoteBlock ? RemoteContentPolicy.Block : RemoteAllow ? RemoteContentPolicy.Allow : RemoteContentPolicy.Ask,
            BlockTrackers = BlockTrackers,
            PlainText = PlainText,
            ConfirmLinks = ConfirmNever ? LinkConfirmation.Never : ConfirmAlways ? LinkConfirmation.Always : LinkConfirmation.Suspicious,
            CleanLinks = CleanLinks,
            WarnSuspicious = WarnSuspicious,
            HideTimeZone = HideTimeZone,
            HideSpamNote = !ShowSpamNote,
            TrustedSenders = [.. TrustedSenders],
        });
    }
}
