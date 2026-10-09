using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Neruna.Core.Diagnostics;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// Catches what slips through. A crash is written to the data directory ("crashes") while the app goes down and
/// offered at the next start; an error on the UI thread once the window is up is caught – Neruna keeps running and
/// offers the report right away. Nothing is sent without the setting or the user's yes (see CrashReportService).
/// </summary>
internal static class CrashHandler
{
    private const int LogLines = 80;

    private static AppOptions? _options;

    public static CrashReportStore Store { get; private set; } = new(Path.Combine(Path.GetTempPath(), "neruna-crashes"));

    /// <summary>Set by AppServices once logging is up.</summary>
    public static ILogger? Logger { get; set; }

    /// <summary>The main window is shown: from now on UI errors are caught instead of ending the app.</summary>
    public static bool UiReady { get; set; }

    /// <summary>An error was caught and stored (UI thread).</summary>
    public static event EventHandler? Caught;

    /// <summary>First thing in Main – also crashes while starting are kept.</summary>
    public static void Install(AppOptions options)
    {
        _options = options;
        Store = new CrashReportStore(Path.Combine(options.DataDirectory, "crashes"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Logger?.LogCritical(ex, "Unhandled exception");
                Record(ex, fatal: true);
            }
        };
    }

    /// <summary>After Avalonia is set up (the dispatcher exists).</summary>
    public static void InstallUi() =>
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (!UiReady || e.Exception is OutOfMemoryException or StackOverflowException)
            {
                return; // ends the app; the AppDomain handler keeps the report
            }

            e.Handled = true;
            Logger?.LogError(e.Exception, "Unhandled exception on the UI thread (caught, Neruna keeps running)");
            Record(e.Exception, fatal: false);
            Caught?.Invoke(null, EventArgs.Empty);
        };

    /// <summary>A failed background task nobody waited for: kept for the next offer, unless it is just the network.</summary>
    public static void RecordUnobserved(Exception exception)
    {
        var inner = exception is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerException! : exception;
        if (inner is OperationCanceledException or HttpRequestException or IOException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return;
        }

        Record(inner, fatal: false);
    }

    private static void Record(Exception exception, bool fatal) =>
        Store.Save(CrashReportBuilder.Capture(exception, fatal, UpdateService.AppVersion, LogTail(), DateTimeOffset.Now));

    // The last lines of today's log (the file logger keeps writing; read shared).
    private static string? LogTail()
    {
        try
        {
            if (_options is null)
            {
                return null;
            }

            var file = Path.Combine(_options.LogDirectory, $"neruna-{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(file))
            {
                return null;
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 64_000), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');
            return string.Join('\n', lines.Skip(1).TakeLast(LogLines)).TrimEnd();
        }
#pragma warning disable CA1031 // never fail while crashing
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
