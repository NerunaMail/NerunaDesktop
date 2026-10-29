using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Neruna.Core.Security;
using Neruna.Storage;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Einstellungen → Profile: Neruna more than once side by side, each with its own accounts and settings and in its own
/// window – e.g. "Business" on one screen and "Privat" on the other.
/// </summary>
internal sealed partial class ProfilesViewModel(AppOptions options, ICredentialStore credentials, ILogger<ProfilesViewModel> logger) : ViewModelBase
{
    private ProfileList? _list;

    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    /// <summary>The demo has no profiles.</summary>
    public bool IsAvailable => options.Root is not null && options.Profile is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>"Bei jedem Start Profil abfragen" (only asked when there are several).</summary>
    [ObservableProperty]
    public partial bool AskAtStart { get; set; }

    private bool _loading;

    partial void OnAskAtStartChanged(bool value)
    {
        if (!_loading && IsAvailable)
        {
            ProfileList.Load(options.Root!).SetAskAtStart(value);
        }
    }

    public void Reload()
    {
        if (!IsAvailable)
        {
            return;
        }

        _list = ProfileList.Load(options.Root!);
        _loading = true;
        AskAtStart = _list.AskAtStart;
        _loading = false;
        Profiles.Clear();
        foreach (var profile in _list.All)
        {
            Profiles.Add(new ProfileItem(this, profile, profile.Id == _list.DefaultId, profile.Id == options.Profile!.Id, _list.DirectoryOf(profile)));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Profiles added, renamed or deleted.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The other profiles that no window has open (offered next to the workspaces). Asked of each profile's Neruna on
    /// a background thread: an answer that does not come takes a moment.
    /// </summary>
    public async Task<IReadOnlyList<Profile>> GetClosedOthersAsync()
    {
        if (!IsAvailable)
        {
            return [];
        }

        var list = ProfileList.Load(options.Root!);
        var others = list.All.Where(p => p.Id != options.Profile!.Id).Select(p => (Profile: p, Folder: list.DirectoryOf(p))).ToList();
        return await Task.Run(() => others.Where(o => !Infrastructure.SingleInstance.IsRunning(o.Folder)).Select(o => o.Profile).ToList());
    }

    private bool CanAdd() => NewName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        Error = null;
        try
        {
            _list = ProfileList.Load(options.Root!);
            _list.Add(NewName);
            NewName = string.Empty;
            Reload();
        }
        catch (ArgumentException)
        {
            Error = T("Ein Profil mit diesem Namen gibt es schon.");
        }
        catch (IOException ex)
        {
            Error = T("Das Profil konnte nicht angelegt werden: ") + ex.Message;
        }
    }

    internal void Rename(ProfileItem item, string name)
    {
        Error = null;
        try
        {
            ProfileList.Load(options.Root!).Rename(item.Profile.Id, name);
            Reload();
        }
        catch (ArgumentException)
        {
            Error = name.Trim().Length == 0 ? T("Bitte einen Namen eingeben.") : T("Ein Profil mit diesem Namen gibt es schon.");
        }
    }

    internal void SetDefault(ProfileItem item)
    {
        ProfileList.Load(options.Root!).SetDefault(item.Profile.Id);
        Reload();
    }

    /// <summary>Another Neruna with this profile; if it runs already, its window comes to the front.</summary>
    internal void Open(ProfileItem item) => Open(item.Profile);

    /// <inheritdoc cref="Open(ProfileItem)"/>
    internal void Open(Profile profile)
    {
        Error = null;
        if (Environment.ProcessPath is not { } program)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(program) { ArgumentList = { ProfileList.Argument, profile.Id.ToString() }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            logger.LogWarning(ex, "Could not start the profile {Profile}", profile.Name);
            Error = T("Das Profil konnte nicht geöffnet werden: ") + ex.Message;
        }
    }

    internal async Task DeleteAsync(ProfileItem item)
    {
        Error = null;
        if (Infrastructure.SingleInstance.IsRunning(item.Folder))
        {
            Error = F("«{0}» ist gerade geöffnet. Bitte das Fenster zuerst schliessen.", item.Name);
            item.ConfirmDelete = false;
            return;
        }

        try
        {
            // Its passwords are in the same credential store, under the ids the profile made.
            await ProfileList.Load(options.Root!).RemoveAsync(item.Profile.Id, profile =>
                ProfileCredentialStore.DeleteAllAsync(credentials is ProfileCredentialStore scoped ? scoped.Inner : credentials, item.Folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = T("Das Profil konnte nicht vollständig gelöscht werden: ") + ex.Message;
        }

        Reload();
    }

    /// <summary>
    /// For a shortcut on the desktop or in the task bar: starts Neruna with this profile. With the id, so the shortcut
    /// still works after renaming.
    /// </summary>
    internal static string CommandOf(Profile profile) =>
        $"\"{Environment.ProcessPath}\" {ProfileList.Argument} {profile.Id}";
}

internal sealed partial class ProfileItem(ProfilesViewModel owner, Profile profile, bool isDefault, bool isCurrent, string folder) : ObservableObject
{
    public Profile Profile { get; } = profile;

    public string Name => Profile.Name;

    public bool IsDefault { get; } = isDefault;

    /// <summary>The profile of this window.</summary>
    public bool IsCurrent { get; } = isCurrent;

    public string Folder { get; } = folder;

    public string Command => ProfilesViewModel.CommandOf(Profile);

    public string? Notes => (IsCurrent, IsDefault) switch
    {
        (true, true) => T("dieses Fenster · beim Start"),
        (true, false) => T("dieses Fenster"),
        (false, true) => T("beim Start"),
        _ => null,
    };

    public bool CanOpen => !IsCurrent;

    public bool CanDelete => !IsCurrent && !Profile.IsStandard;

    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string NewName { get; set; } = profile.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteText))]
    public partial bool ConfirmDelete { get; set; }

    public string DeleteText => ConfirmDelete ? T("Wirklich löschen?") : T("Löschen");

    [RelayCommand]
    private void Open() => owner.Open(this);

    [RelayCommand]
    private void SetDefault() => owner.SetDefault(this);

    [RelayCommand]
    private void StartRename()
    {
        NewName = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void Rename()
    {
        IsRenaming = false;
        if (NewName.Trim() != Name)
        {
            owner.Rename(this, NewName);
        }
    }

    /// <summary>Two clicks: the profile's accounts, mails and passwords go with it.</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!ConfirmDelete)
        {
            ConfirmDelete = true;
            return;
        }

        await owner.DeleteAsync(this);
    }
}
