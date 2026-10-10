using Avalonia;

namespace Neruna.Desktop;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack first: during install/update/uninstall it runs its hooks and exits; it also applies an update that
        // was downloaded before the last exit.
        var velopack = Velopack.VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            velopack = velopack.OnBeforeUninstallFastCallback(_ => Infrastructure.DefaultMailApp.Current?.Unregister());
        }

        velopack.SetArgs(args).Run();
        App.Options = AppOptions.FromArgs(args);

        // Already running (e.g. a mailto: link or .eml file opened from the system): hand over and end.
        if (Infrastructure.SingleInstance.TryHandOver(App.Options, args))
        {
            return;
        }

        Infrastructure.CrashHandler.Install(App.Options);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
