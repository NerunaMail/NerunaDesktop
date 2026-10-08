using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// Updates from the GitHub releases of NerunaDesktop (Velopack): checked at start and every few hours, downloaded in
/// the background, installed on "Jetzt neu starten" – or automatically at the next start. Only for the installed app;
/// a copy run from a ZIP or the development build says so instead.
/// </summary>
internal sealed partial class UpdateService : ObservableObject
{
    public const string ReleasesUrl = "https://github.com/NerunaMail/NerunaDesktop";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    private readonly ILogger<UpdateService> _logger;
    private readonly UpdateManager? _manager;
    private readonly DispatcherTimer _timer;
    private UpdateInfo? _ready;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        try
        {
            // Pre-releases included: Neruna is in beta, every release is one for now.
            var manager = new UpdateManager(new GithubSource(ReleasesUrl, accessToken: null, prerelease: true));
            _manager = manager.IsInstalled ? manager : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Update manager unavailable");
        }

        CurrentVersion = _manager?.CurrentVersion?.ToString() ?? AppVersion;
        StatusText = _manager is null
            ? "Automatische Updates gibt es nur in der installierten Version (Setup von neruna.org)."
            : "Noch nicht nach Updates gesucht.";
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    /// <summary>"Jetzt neu starten" was clicked: the window closes (asking about drafts) and then calls <see cref="ApplyAndRestart"/>.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>The version of this build (from the assembly; without the commit hash).</summary>
    public static string AppVersion =>
        (typeof(UpdateService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public bool CanUpdate => _manager is not null;

    public string CurrentVersion { get; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    public partial bool IsChecking { get; set; }

    /// <summary>A newer version is downloaded and waits for a restart.</summary>
    [ObservableProperty]
    public partial bool IsReady { get; set; }

    [ObservableProperty]
    public partial string? ReadyText { get; set; }

    /// <summary>First check shortly after start, then every few hours.</summary>
    public void Start()
    {
        if (_manager is null)
        {
            return;
        }

        _timer.Start();
        DispatcherTimer.RunOnce(async () => await CheckAsync(), TimeSpan.FromSeconds(20));
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => CheckAsync(manual: true);

    private bool CanCheck() => CanUpdate && !IsChecking;

    private async Task CheckAsync(bool manual = false)
    {
        if (_manager is null || IsChecking || IsReady)
        {
            return;
        }

        IsChecking = true;
        StatusText = "Suche nach Updates …";
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                StatusText = $"Neruna ist aktuell (geprüft um {DateTime.Now:HH:mm}).";
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            StatusText = $"Version {version} wird heruntergeladen …";
            await _manager.DownloadUpdatesAsync(update, progress => Dispatcher.UIThread.Post(() =>
                StatusText = $"Version {version} wird heruntergeladen … {progress} %"));
            _ready = update;
            ReadyText = $"Version {version} ist bereit";
            StatusText = $"Version {version} ist heruntergeladen – sie wird beim nächsten Start installiert, oder gleich mit «Jetzt neu starten».";
            IsReady = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Offline or GitHub not reachable: quietly try again later; a manual check says what happened.
            _logger.LogWarning(ex, "Update check failed");
            StatusText = manual ? $"Updates konnten nicht geprüft werden: {ex.Message}" : "Updates konnten zuletzt nicht geprüft werden (offline?).";
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void RestartNow() => RestartRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Installs the downloaded version and starts it again (the window has already closed).</summary>
    public void ApplyAndRestart()
    {
        if (_manager is not null && _ready is not null)
        {
            _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
        }
    }
}
