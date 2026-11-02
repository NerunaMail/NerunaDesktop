using Microsoft.Extensions.Logging;

namespace Neruna.Desktop.Infrastructure;

/// <summary>Scripts into a web view whose answer nobody waits for.</summary>
internal static class PageScripts
{
    /// <summary>
    /// Runs on without waiting. The page can be replaced while the script runs (the next mail opened at once, the
    /// editor unloaded): WebKitGTK then fails it with "Unsupported result type". Nothing depends on the answer, so that
    /// is logged, not a crash report.
    /// </summary>
    public static void Forget(this Task task) =>
        task.ContinueWith(
            t => CrashHandler.Logger?.LogDebug(t.Exception, "A script into the page did not finish"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
