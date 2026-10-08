using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// Writes one log file per day to <c>&lt;data&gt;/logs</c>. Deliberately tiny: a background writer, no dependencies.
/// Secrets must never be logged; protocol traces (NERUNA_PROTOCOL_LOG=1) are redacted by MailKit.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;
    private readonly string _directory;
    private readonly LogLevel _minimum;

    public FileLoggerProvider(string directory, LogLevel minimum)
    {
        _directory = directory;
        _minimum = minimum;
        Directory.CreateDirectory(directory);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Neruna log writer" };
        _writer.Start();
    }

    public string CurrentFile => Path.Combine(_directory, $"neruna-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.TryAdd(line);
        }
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _shortCategory = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(' ').Append(Level(logLevel))
                .Append(' ').Append(_shortCategory)
                .Append(": ").Append(formatter(state, exception))
                .AppendLine();
            if (exception is not null)
            {
                line.AppendLine(exception.ToString());
            }

            provider.Enqueue(line.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
