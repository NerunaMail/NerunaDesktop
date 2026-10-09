using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Neruna.Desktop.Infrastructure;
using Neruna.Desktop.ViewModels;
using Neruna.Desktop.Views;

namespace Neruna.Desktop;

internal sealed partial class App : Application
{
    private ServiceProvider? _services;

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
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await splash.ShowStatusAsync(Neruna.Core.Localization.Texts.T("Daten werden geöffnet …"));
            _services = await Task.Run(() => AppServices.BuildAsync(Options));
            desktop.ShutdownRequested += (_, _) => _services.Dispose();

            // The setting decides (e.g. restored from a backup); before any view or view model exists.
            if (await _services.GetRequiredService<Neruna.Core.ISettingsStore>().GetAsync(Neruna.Core.SettingKeys.UiLanguage) is { } language
                && language != LanguageFile.Read(Options.DataDirectory))
            {
                Neruna.Core.Localization.Texts.Use(language);
                LanguageFile.Write(Options.DataDirectory, language);
            }

            await Appearance.LoadAsync(_services.GetRequiredService<Neruna.Core.ISettingsStore>());
            var layout = _services.GetRequiredService<UiLayout>();
            await layout.LoadAsync();
            await _services.GetRequiredService<UiPreferences>().LoadAsync();

            await splash.ShowStatusAsync(Neruna.Core.Localization.Texts.T("Konten und Ordner werden geladen …"));
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            await viewModel.LoadLocalAsync();

            await splash.ShowStatusAsync(Neruna.Core.Localization.Texts.T("Oberfläche wird aufgebaut …"));
            var main = new MainWindow { DataContext = viewModel };
            layout.TrackWindow(main, "window.main", withPosition: true);
            desktop.MainWindow = main;
            main.Show();
            splash.Close();

            await viewModel.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
