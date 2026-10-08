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
        // The UI is German-only until localization lands (docs/architecture.md, open decision 5).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("de-CH");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Splash first, so the user sees something while the database opens and the window is built.
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await splash.ShowStatusAsync("Daten werden geöffnet …");
            _services = await Task.Run(() => AppServices.BuildAsync(Options));
            desktop.ShutdownRequested += (_, _) => _services.Dispose();

            await Appearance.LoadAsync(_services.GetRequiredService<Neruna.Core.ISettingsStore>());
            var layout = _services.GetRequiredService<UiLayout>();
            await layout.LoadAsync();
            await _services.GetRequiredService<UiPreferences>().LoadAsync();

            await splash.ShowStatusAsync("Konten und Ordner werden geladen …");
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            await viewModel.LoadLocalAsync();

            await splash.ShowStatusAsync("Oberfläche wird aufgebaut …");
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
