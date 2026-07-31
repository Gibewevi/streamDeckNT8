using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace StreamDeckBridge.Logging;

/// <summary>
/// Sends every <see cref="ILogger"/> call to a daily file, in the line format shared with the
/// plugin and the NT8 add-on:
///
///   2026-07-31 14:23:45.123 | INFO  | bridge | MessageRouter | message | key=value
///
/// The console provider stays in place for interactive runs, but the bridge is normally
/// auto-launched detached by the plugin (stdio: 'ignore'), so the file is the only trace that
/// survives.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly DailyFileWriter _writer;
    private readonly LogLevel _minLevel;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string directory, LogLevel minLevel, int retentionDays, int maxFileSizeMb)
    {
        _writer = new DailyFileWriter(directory, "bridge", retentionDays, maxFileSizeMb);
        _minLevel = minLevel;
    }

    public string CurrentPath => _writer.CurrentPath;

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(_writer, ShortCategory(name), _minLevel));

    /// <summary>"StreamDeckBridge.MessageRouter" → "MessageRouter".</summary>
    private static string ShortCategory(string categoryName)
    {
        var dot = categoryName.LastIndexOf('.');
        return dot >= 0 && dot < categoryName.Length - 1 ? categoryName[(dot + 1)..] : categoryName;
    }

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger : ILogger
    {
        private readonly DailyFileWriter _writer;
        private readonly string _category;
        private readonly LogLevel _minLevel;

        public FileLogger(DailyFileWriter writer, string category, LogLevel minLevel)
        {
            _writer = writer;
            _category = category;
            _minLevel = minLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var sb = new StringBuilder(256);
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" | ").Append(LevelLabel(logLevel))
              .Append(" | bridge | ").Append(_category)
              .Append(" | ").Append(Sanitize(formatter(state, exception)));

            if (exception != null)
            {
                // Type and message on the log line, stack trace indented underneath so the
                // line stays greppable while the detail needed to fix it is right there.
                sb.Append(" | exception=").Append(exception.GetType().Name)
                  .Append(" message=").Append(Sanitize(exception.Message));

                var trace = exception.ToString();
                foreach (var traceLine in trace.Split('\n'))
                {
                    var trimmed = traceLine.TrimEnd('\r');
                    if (trimmed.Length > 0) sb.Append("\n    ").Append(trimmed);
                }
            }

            _writer.Write(sb.ToString());
        }

        /// <summary>Keeps one event on one line — embedded newlines break log parsing.</summary>
        private static string Sanitize(string value) =>
            value.Contains('\n') || value.Contains('\r')
                ? value.Replace("\r\n", " ⏎ ").Replace('\n', '⏎').Replace('\r', '⏎')
                : value;

        private static string LevelLabel(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => "?????"
        };
    }
}
