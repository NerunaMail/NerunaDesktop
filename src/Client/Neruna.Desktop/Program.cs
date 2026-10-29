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

        // Another profile running: a downloaded update waits for "Jetzt neu starten" (or a start with nothing else
        // open) – installing it now would end the running one without asking.
        if (OtherNerunaRunning())
        {
            velopack = velopack.SetAutoApplyOnStartup(false);
        }

        velopack.SetArgs(args).Run();
        App.Options = AppOptions.FromArgs(args);

        // Already running (e.g. a mailto: link or .eml file opened from the system): hand over and end. Asking for the
        // profile first: done once it is chosen (App).
        if (!App.Options.AskForProfile)
        {
            if (Infrastructure.SingleInstance.TryHandOver(App.Options, args))
            {
                return;
            }

            Infrastructure.CrashHandler.Install(App.Options);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static bool OtherNerunaRunning()
    {
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var same = System.Diagnostics.Process.GetProcessesByName(self.ProcessName);
            var others = same.Length > 1;
            foreach (var process in same)
            {
                process.Dispose();
            }

            return others;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
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
