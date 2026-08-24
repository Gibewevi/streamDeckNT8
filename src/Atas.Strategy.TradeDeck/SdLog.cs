using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// The strategy's log file, written to %APPDATA%\StreamDeckTrader\logs\addon-YYYY-MM-DD.log.
///
/// SAME PATH AND SAME LINE FORMAT AS THE NINJATRADER ADD-ON, deliberately. The diagnostic flow in
/// docs/logging-strategy.md correlates the three components' files by timestamp and by requestId;
/// a fourth file name, or a fifth column, would break every existing procedure for no gain. The
/// component column stays "addon" for the same reason: what changed is the platform underneath,
/// not the role in the protocol.
///
/// Files are UTF-8 WITH a BOM — the opposite of the journal spool. These are read by humans in
/// Notepad, VS Code and Get-Content, where a missing BOM mangles the accents.
/// </summary>
internal static class SdLog
{
    private const int LevelTrace = 0;
    private const int LevelDebug = 1;
    private const int LevelInfo = 2;
    private const int LevelWarn = 3;
    private const int LevelError = 4;

    private const string NoCategory = "-";

    /// <summary>
    /// Beyond this, the day's file is rolled. A single unbounded file becomes unopenable exactly
    /// on the day something went wrong badly enough to be worth reading.
    /// </summary>
    private const long MaxBytes = 32L * 1024 * 1024;

    private static readonly object Sync = new();

    /// <summary>
    /// TRACE is not written by default: the state publisher runs twice a second, and its frames
    /// alone would be hundreds of thousands of lines a day. Raised through TD_LOG_LEVEL when the
    /// whole WebSocket flow has to be replayed.
    /// </summary>
    private static int _fileLevel = ReadLevelFromEnvironment();

    private static StreamWriter? _writer;
    private static DateTime _openDate = DateTime.MinValue;
    private static int _rollIndex;
    private static string _currentPath = string.Empty;

    public static void Trace(string format, params object[] args) => Write(LevelTrace, NoCategory, Format(format, args), null);
    public static void Debug(string format, params object[] args) => Write(LevelDebug, NoCategory, Format(format, args), null);
    public static void Info(string format, params object[] args) => Write(LevelInfo, NoCategory, Format(format, args), null);
    public static void Warn(string format, params object[] args) => Write(LevelWarn, NoCategory, Format(format, args), null);
    public static void Error(string format, params object[] args) => Write(LevelError, NoCategory, Format(format, args), null);

    public static void Event(string category, string format, params object[] args) => Write(LevelInfo, category, Format(format, args), null);
    public static void EventWarn(string category, string format, params object[] args) => Write(LevelWarn, category, Format(format, args), null);
    public static void TraceEvent(string category, string format, params object[] args) => Write(LevelTrace, category, Format(format, args), null);
    public static void Fail(string category, Exception? ex, string format, params object[] args) => Write(LevelError, category, Format(format, args), ex);

    public static void SessionHeader(string bridgeUrl)
    {
        lock (Sync)
        {
            try { EnsureWriter(); } catch { CloseWriter(); }
        }

        Event("Session", "=== TradeDeck ATAS strategy session started === pid={0} assembly={1} bridge={2} logFile={3}",
            Process.GetCurrentProcess().Id,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?",
            bridgeUrl,
            string.IsNullOrEmpty(_currentPath) ? "(unavailable)" : _currentPath);
    }

    private static int ReadLevelFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("TD_LOG_LEVEL");
        return (raw ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "TRACE" => LevelTrace,
            "DEBUG" => LevelDebug,
            "INFO" => LevelInfo,
            "WARN" => LevelWarn,
            "ERROR" => LevelError,
            _ => LevelDebug,
        };
    }

    private static string Format(string format, object[] args)
    {
        if (args == null || args.Length == 0) return format;
        try { return string.Format(CultureInfo.InvariantCulture, format, args); }
        // A malformed format string must still produce a line, not swallow the event.
        catch (FormatException) { return format + " [unformattable args]"; }
    }

    private static void Write(int level, string category, string message, Exception? ex)
    {
        if (level < _fileLevel) return;

        lock (Sync)
        {
            try
            {
                var writer = EnsureWriter();
                if (writer == null) return;

                var line = new StringBuilder(256);
                line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(Label(level))
                    .Append(" | addon | ").Append(string.IsNullOrEmpty(category) ? NoCategory : category)
                    .Append(" | ").Append(Sanitize(message));

                if (ex != null)
                {
                    line.Append(" | exception=").Append(ex.GetType().Name)
                        .Append(" message=").Append(Sanitize(ex.Message));

                    foreach (var traceLine in ex.ToString().Split('\n'))
                    {
                        var trimmed = traceLine.TrimEnd('\r');
                        if (trimmed.Length > 0) line.Append("\n    ").Append(trimmed);
                    }
                }

                writer.WriteLine(line.ToString());
            }
            catch
            {
                // Drop the line and retry with a fresh handle next time (file moved, disk full,
                // permissions changed). Logging never propagates a failure to the caller — least of
                // all to an order path.
                CloseWriter();
            }
        }
    }

    private static string Label(int level) => level switch
    {
        LevelTrace => "TRACE",
        LevelDebug => "DEBUG",
        LevelInfo => "INFO ",
        LevelWarn => "WARN ",
        _ => "ERROR",
    };

    /// <summary>A newline inside a message would forge a second log line.</summary>
    private static string Sanitize(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", " ").Replace("\n", " ");

    private static StreamWriter? EnsureWriter()
    {
        var today = DateTime.Now.Date;

        if (_writer != null && today != _openDate)
        {
            CloseWriter();
            _rollIndex = 0;
        }

        if (_writer != null && _writer.BaseStream.Length >= MaxBytes)
        {
            CloseWriter();
            _rollIndex++;
        }

        if (_writer != null) return _writer;

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamDeckTrader", "logs");
        Directory.CreateDirectory(directory);
        _openDate = today;

        var name = _rollIndex == 0
            ? string.Format(CultureInfo.InvariantCulture, "addon-{0:yyyy-MM-dd}.log", today)
            : string.Format(CultureInfo.InvariantCulture, "addon-{0:yyyy-MM-dd}.{1}.log", today, _rollIndex);

        _currentPath = Path.Combine(directory, name);

        // FileShare.ReadWrite: the file must stay readable while it is being written — the whole
        // point is to look at it while the problem is happening.
        var stream = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(true)) { AutoFlush = true };
        return _writer;
    }

    private static void CloseWriter()
    {
        try { _writer?.Dispose(); } catch { /* nothing useful left to do */ }
        _writer = null;
    }
}
