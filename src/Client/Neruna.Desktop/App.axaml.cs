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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Language: as last chosen (file next to the database), the system's at first; see Neruna.Core.Localization.
            Neruna.Core.Localization.Texts.Use(LanguageFile.Read(Options.DataDirectory));

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
                var rest = Neruna.Storage.ProfileList.WithoutProfile(args);
                Open(rest.Length == 0 ? [SystemOpen.Activate] : rest);
            }, Shutdown.Token);
            if (this.TryGetFeature<Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is Avalonia.Controls.ApplicationLifetimes.ProtocolActivatedEventArgs protocol)
                    {
                        Open([protocol.Uri.OriginalString]);
                    }
                    else if (e is Avalonia.Controls.ApplicationLifetimes.FileActivatedEventArgs files)
                    {
                        Open([.. files.Files.Select(f => f.TryGetLocalPath()).OfType<string>()]);
                    }
                };
            }

            desktop.ShutdownRequested += (_, _) => Shutdown.Cancel();
            Infrastructure.DefaultMailApp.Current?.Refresh();

            await viewModel.StartAsync();
            _ = _services.GetRequiredService<PrivacyService>().StartPhishingListAsync();
            Open(desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
