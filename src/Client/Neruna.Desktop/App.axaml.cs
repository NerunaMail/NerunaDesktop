using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Neruna.Desktop.Infrastructure;
using Neruna.Desktop.ViewModels;
using Neruna.Desktop.Views;

namespace Neruna.Desktop;

internal sealed partial class App : Application
{
    private ServiceProvider? _services;
    private static readonly CancellationTokenSource Shutdown = new();

    public static AppOptions Options { get; set; } = AppOptions.FromArgs([]);

    /// <summary>macOS, another Neruna already runs: only pass on what the system opens (a link, a file), then end.</summary>
    public static bool RelayOnly { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // macOS hands mailto: links and files over as system events – also the one Neruna was started for, while
            // the database still opens. Listened to from the start; kept until the window can open them.
            var early = new List<string>();
            Action<IReadOnlyList<string>>? open = null;
            if (this.TryGetFeature<Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    IReadOnlyList<string> items = e switch
                    {
                        Avalonia.Controls.ApplicationLifetimes.ProtocolActivatedEventArgs protocol => [protocol.Uri.OriginalString],
                        Avalonia.Controls.ApplicationLifetimes.FileActivatedEventArgs files => [.. files.Files.Select(f => f.TryGetLocalPath()).OfType<string>()],
                        _ => [],
                    };
                    if (items.Count == 0)
                    {
                        return;
                    }

                    if (open is null)
                    {
                        early.AddRange(items);
                    }
                    else
                    {
                        open(items);
                    }
                };
            }

            if (RelayOnly)
            {
                Relay(desktop, early);
                return;
            }

            // Language: as last chosen (file next to the database), the system's at first; see Neruna.Core.Localization.
            Neruna.Core.Localization.Texts.Use(LanguageFile.Read(Options.DataDirectory));
            KeyNames.Use();

            // "Bei jedem Start Profil abfragen": which one, before its data is opened. Closed without a choice: end.
            if (Options.AskForProfile)
            {
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                var chosen = await ProfileChooserWindow.AskAsync(Neruna.Storage.ProfileList.Load(Options.Root!));
                if (chosen is null)
                {
                    desktop.Shutdown();
                    return;
                }

                Options = Options.WithProfile(chosen);
                if (SingleInstance.TryHandOver(Options, [.. (desktop.Args ?? []).Where(SystemOpen.IsOpenable), .. early]))
                {
                    desktop.Shutdown(); // already open: its window came to the front
                    return;
                }

                CrashHandler.Install(Options);
                Neruna.Core.Localization.Texts.Use(LanguageFile.Read(Options.DataDirectory));
                KeyNames.Use();
            }

            // Splash first, so the user sees something while the database opens and the window is built.
            ScreenFit.Install();
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await splash.ShowStatusAsync(Neruna.Core.Localization.Texts.T("Daten werden geöffnet …"));
            _services = await Task.Run(() => AppServices.BuildAsync(Options));
            desktop.ShutdownRequested += (_, _) => _services.Dispose();

            // Copies of opened attachments (also decrypted ones) are kept a day, for the program that opened them.
            _ = Task.Run(() =>
            {
                try
                {
                    Neruna.Core.Security.OpenedAttachments.CleanUp(TimeSpan.FromDays(1));
                    Neruna.Core.Mail.MapiRequest.CleanUp(Path.GetTempPath(), TimeSpan.FromDays(1));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Next start.
                }
            });

            // The setting decides (e.g. restored from a backup); before any view or view model exists.
            if (await _services.GetRequiredService<Neruna.Core.ISettingsStore>().GetAsync(Neruna.Core.SettingKeys.UiLanguage) is { } language
                && language != LanguageFile.Read(Options.DataDirectory))
            {
                Neruna.Core.Localization.Texts.Use(language);
                KeyNames.Use();
                LanguageFile.Write(Options.DataDirectory, language);
            }

            await Appearance.LoadAsync(_services.GetRequiredService<Neruna.Core.ISettingsStore>());
            MailExport.Settings = _services.GetRequiredService<Neruna.Core.ISettingsStore>();
            Neruna.Core.Privacy.PrivacyStatistics.Current = _services.GetRequiredService<Neruna.Core.Privacy.PrivacyStatistics>();
            Editor.HtmlEditor.PlainTextOnly = await Neruna.Core.SettingsStoreExtensions.GetBoolAsync(_services.GetRequiredService<Neruna.Core.ISettingsStore>(), Neruna.Core.SettingKeys.ComposePlainText);
            var layout = _services.GetRequiredService<UiLayout>();
            await layout.LoadAsync();
            await _services.GetRequiredService<UiPreferences>().LoadAsync();

            await splash.ShowStatusAsync(Neruna.Core.Localization.Texts.T("Konten und Ordner werden geladen …"));
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            await viewModel.LoadLocalAsync();

            await splash.ShowStatusAsync(Neruna.Core.Localization.Texts.T("Oberfläche wird aufgebaut …"));
            var main = new MainWindow { DataContext = viewModel };
            if (Options.Profile is { IsStandard: false } profile)
            {
                // Two Neruna side by side: the title tells them apart (task bar, window switcher).
                main.Title = "Neruna – " + profile.Name;
            }

            layout.TrackWindow(main, "window.main", withPosition: true);
            Views.CalendarWindow.Attach(viewModel, layout, main);
            desktop.MainWindow = main;

            // Mail and draft windows are not owned by the main window (so they do not stay in front of it); closing
            // the main window still ends Neruna.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            main.Show();
            splash.Close();
            Infrastructure.CrashHandler.InstallUi();
            Infrastructure.CrashHandler.UiReady = true;

            // mailto: links and .eml files: from the command line, from later starts (handed over) and – on macOS –
            // as system events.
            void Open(IReadOnlyList<string> items) => Dispatcher.UIThread.Post(async () =>
            {
                if (items.Any(SystemOpen.IsOpenable))
                {
                    main.Activate();
                    if (main.WindowState == Avalonia.Controls.WindowState.Minimized)
                    {
                        main.WindowState = Avalonia.Controls.WindowState.Normal;
                    }
                }

                await viewModel.OpenFromSystemAsync(items);
            });
            // A later start of this profile (its shortcut, "In neuem Fenster öffnen") brings the window to the front.
            Infrastructure.SingleInstance.Listen(Options, args =>
            {
                // Another profile installs an update: close as if the user did (drafts are asked about).
                if (args.Contains(SystemOpen.CloseForUpdate))
                {
                    Dispatcher.UIThread.Post(main.Close);
                    return;
                }

                var rest = Neruna.Storage.ProfileList.WithoutProfile(args);
                Open(rest.Length == 0 ? [SystemOpen.Activate] : rest);
            }, Shutdown.Token);
            desktop.ShutdownRequested += (_, _) => Shutdown.Cancel();
            Infrastructure.DefaultMailApp.Current?.Refresh();
            Infrastructure.LinkProtocol.Register();

            // A file or link the system started Neruna with opens at once, not only after the first sync of all accounts.
            var starting = viewModel.StartAsync();
            Open([.. desktop.Args ?? [], .. early]);
            open = Open;
            // Installed from the page /connect: the link it copied (only on the very first start, only a connection link).
            if (!(desktop.Args ?? []).Concat(early).Any(SystemOpen.IsConnectLink) && await viewModel.IsFirstStartAsync() && main.Clipboard is { } clipboard)
            {
                try
                {
                    if (await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard) is { } copied
                        && Neruna.Core.Cloud.CloudConnectLink.TryParse(copied) is not null)
                    {
                        Open([copied.Trim()]);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
                {
                    // No clipboard: the link can still be opened again from the page.
                }
            }

            await starting;
            _ = _services.GetRequiredService<PrivacyService>().StartPhishingListAsync();

            // After an update: the other profiles that were open come back as well.
            foreach (var id in SystemOpen.ReopenIn(desktop.Args ?? []).Where(id => id != Options.Profile?.Id))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                    {
                        ArgumentList = { Neruna.Storage.ProfileList.Argument, id.ToString() },
                        UseShellExecute = false,
                    });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
                {
                    // That profile opens with its next start.
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // No window: what macOS hands over in the first moments goes to the running Neruna (or just brings it to the front).
    private static void Relay(IClassicDesktopStyleApplicationLifetime desktop, List<string> received)
    {
        desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
        var waited = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            waited += 100;
            if (received.Count == 0 && waited < 3000)
            {
                return;
            }

            timer.Stop();
            string[] items = received.Count > 0 ? [.. received] : [SystemOpen.Activate];
            _ = SingleInstance.TryHandOver(Options, items) || SingleInstance.TryHandOverToOpenProfile(Options, items);
            desktop.Shutdown();
        };
        timer.Start();
    }
}
