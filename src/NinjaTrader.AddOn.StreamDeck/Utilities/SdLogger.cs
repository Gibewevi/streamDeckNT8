using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities
{
    /// <summary>
    /// Structured logger for the NT8 add-on.
    ///
    /// Writes to two places:
    ///   - NinjaTrader's output window, prefixed with [StreamDeck] (live troubleshooting)
    ///   - one file per day in %APPDATA%\StreamDeckTrader\logs\addon-YYYY-MM-DD.log
    ///
    /// The output window is cleared on every NinjaTrader restart and holds a limited number of
    /// lines, so it cannot answer "what happened this morning?". The file can: it uses the same
    /// line format as the bridge and the plugin, so the three files of a day can be sorted
    /// together to replay an entire session.
    ///
    ///   2026-07-31 14:23:45.123 | INFO  | addon | OrderSubmit | message | key=value
    ///
    /// Nothing here is allowed to throw: a logging failure must never abort an order.
    /// </summary>
    public static class SdLogger
    {
        public const int LevelTrace = 0;
        public const int LevelDebug = 1;
        public const int LevelInfo = 2;
        public const int LevelWarn = 3;
        public const int LevelError = 4;

        private const string Prefix = "[StreamDeck]";
        private const string NoCategory = "-";

        private static readonly object Sync = new object();

        private static int _fileLevel = LevelDebug;
        private static int _outputLevel = LevelDebug;
        private static int _retentionDays = 30;
        private static long _maxBytes = 25L * 1024L * 1024L;
        private static string _directory;

        private static StreamWriter _writer;
        private static DateTime _openDate = DateTime.MinValue;
        private static int _rollIndex;
        private static string _currentPath = string.Empty;
        private static bool _configLoaded;

        /// <summary>Full path of today's log file, or "" if the file sink is unavailable.</summary>
        public static string LogFilePath
        {
            get { return _currentPath; }
        }

        // --- Public API -----------------------------------------------------------------

        public static void Trace(string message)
        {
            Write(LevelTrace, NoCategory, message, null);
        }

        public static void Trace(string format, params object[] args)
        {
            Write(LevelTrace, NoCategory, Format(format, args), null);
        }

        public static void Debug(string message)
        {
            Write(LevelDebug, NoCategory, message, null);
        }

        public static void Debug(string format, params object[] args)
        {
            Write(LevelDebug, NoCategory, Format(format, args), null);
        }

        public static void Info(string message)
        {
            Write(LevelInfo, NoCategory, message, null);
        }

        public static void Info(string format, params object[] args)
        {
            Write(LevelInfo, NoCategory, Format(format, args), null);
        }

        public static void Warn(string message)
        {
            Write(LevelWarn, NoCategory, message, null);
        }

        public static void Warn(string format, params object[] args)
        {
            Write(LevelWarn, NoCategory, Format(format, args), null);
        }

        public static void Error(string message)
        {
            Write(LevelError, NoCategory, message, null);
        }

        public static void Error(string format, params object[] args)
        {
            Write(LevelError, NoCategory, Format(format, args), null);
        }

        public static void Error(Exception ex, string message)
        {
            Write(LevelError, NoCategory, message, ex);
        }

        /// <summary>
        /// Logs a named event with its context — the form used for anything a post-mortem
        /// would search for (orders, position changes, refusals, connection transitions).
        /// </summary>
        public static void Event(string category, string message)
        {
            Write(LevelInfo, category, message, null);
        }

        public static void Event(string category, string format, params object[] args)
        {
            Write(LevelInfo, category, Format(format, args), null);
        }

        /// <summary>Same as <see cref="Event"/> but for a refusal or an anomaly.</summary>
        public static void EventWarn(string category, string format, params object[] args)
        {
            Write(LevelWarn, category, Format(format, args), null);
        }

        /// <summary>Logs a failure with its exception type, message and full stack trace.</summary>
        public static void Fail(string category, Exception ex, string format, params object[] args)
        {
            Write(LevelError, category, Format(format, args), ex);
        }

        /// <summary>Chatty per-tick detail (state publishing). Below the default file level.</summary>
        public static void TraceEvent(string category, string format, params object[] args)
        {
            Write(LevelTrace, category, Format(format, args), null);
        }

        /// <summary>
        /// Announces the session in the log file: without this line a file that starts mid-flow
        /// gives no clue which build, account or bridge it belongs to.
        /// </summary>
        public static void LogSessionHeader(string bridgeUrl)
        {
            EnsureConfig();
            OpenFileSink();
            Event("Session", "=== StreamDeck Add-On session started === pid={0} assembly={1} bridge={2} logFile={3}",
                GetProcessId(), GetAssemblyVersion(), bridgeUrl, string.IsNullOrEmpty(_currentPath) ? "(unavailable)" : _currentPath);
        }

        // --- Internals ------------------------------------------------------------------

        private static string Format(string format, object[] args)
        {
            if (args == null || args.Length == 0) return format;
            try
            {
                return string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (FormatException)
            {
                // A malformed format string must still produce a log line, not swallow the event.
                return format + " [unformattable args]";
            }
        }

        private static void Write(int level, string category, string message, Exception ex)
        {
            EnsureConfig();

            // File first: it is the durable sink, and it must not depend on NinjaTrader's output
            // window being usable. The guard around the output call is deliberately here rather
            // than only inside the callee — a type-initialization failure in the NinjaTrader
            // assemblies is raised at the call boundary, before the callee's own try block is
            // ever entered, and would otherwise propagate straight into an order-submission path.
            if (level >= _fileLevel) WriteToFile(level, category, message, ex);

            if (level >= _outputLevel)
            {
                try { WriteToOutputWindow(level, category, message, ex); }
                catch { /* NT output unavailable — the file already has the line */ }
            }
        }

        private static void WriteToOutputWindow(int level, string category, string message, Exception ex)
        {
            try
            {
                var line = new StringBuilder(160);
                line.Append(Prefix).Append(' ').Append(LevelLabel(level)).Append(" | ");
                if (!string.IsNullOrEmpty(category) && category != NoCategory)
                    line.Append(category).Append(" | ");
                line.Append(message);
                if (ex != null)
                    line.Append(" — ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);

                NinjaTrader.Code.Output.Process(line.ToString(), PrintTo.OutputTab1);
            }
            catch
            {
                // NT output is not available (add-on unloading, or called off the UI thread).
            }
        }

        private static void WriteToFile(int level, string category, string message, Exception ex)
        {
            lock (Sync)
            {
                try
                {
                    var writer = EnsureWriter();
                    if (writer == null) return;

                    var line = new StringBuilder(256);
                    line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                        .Append(" | ").Append(LevelLabel(level))
                        .Append(" | addon | ").Append(string.IsNullOrEmpty(category) ? NoCategory : category)
                        .Append(" | ").Append(Sanitize(message));

                    if (ex != null)
                    {
                        line.Append(" | exception=").Append(ex.GetType().Name)
                            .Append(" message=").Append(Sanitize(ex.Message));

                        var trace = ex.ToString();
                        foreach (var traceLine in trace.Split('\n'))
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
                    // permissions changed). Logging never propagates a failure to the caller.
                    CloseWriter();
                }
            }
        }

        /// <summary>
        /// Opens today's file up front so the header line can name it. Without this the very
        /// first line reports the path as unavailable, which is the one line most likely to be
        /// read when someone is looking for the file in the first place.
        /// </summary>
        private static void OpenFileSink()
        {
            lock (Sync)
            {
                try { EnsureWriter(); }
                catch { CloseWriter(); }
            }
        }

        private static StreamWriter EnsureWriter()
        {
            var today = DateTime.Now.Date;

            if (_writer != null && today != _openDate)
            {
                CloseWriter();
                _rollIndex = 0;
            }

            if (_writer != null && _writer.BaseStream.Length >= _maxBytes)
            {
                CloseWriter();
                _rollIndex++;
            }

            if (_writer != null) return _writer;

            Directory.CreateDirectory(_directory);
            _openDate = today;

            var name = _rollIndex == 0
                ? string.Format("addon-{0:yyyy-MM-dd}.log", today)
                : string.Format("addon-{0:yyyy-MM-dd}.{1}.log", today, _rollIndex);

            _currentPath = Path.Combine(_directory, name);

            // FileShare.ReadWrite: NinjaTrader recompiles NinjaScript on the fly and the previous
            // assembly's handle may outlive this one for a moment. Sharing keeps both appending
            // instead of one of them silently losing every line.
            // UTF-8 with a BOM on a freshly created file (the preamble is skipped when appending
            // to an existing one), so Notepad and PowerShell 5.1 do not read accents as ANSI.
            var stream = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(true));
            _writer.AutoFlush = true;

            PurgeOldFiles();
            return _writer;
        }

        private static void PurgeOldFiles()
        {
            try
            {
                var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
                foreach (var path in Directory.GetFiles(_directory, "addon-*.log"))
                {
                    if (string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.GetLastWriteTime(path) >= cutoff) continue;
                    try { File.Delete(path); }
                    catch { /* held open by another process — retry tomorrow */ }
                }
            }
            catch
            {
                // Retention is best-effort.
            }
        }

        private static void CloseWriter()
        {
            try { if (_writer != null) _writer.Dispose(); }
            catch { }
            _writer = null;
        }

        /// <summary>
        /// Reads the environment overrides shared with the bridge and the plugin, so all three
        /// components can be pointed at the same alternative directory or level in one place.
        /// </summary>
        private static void EnsureConfig()
        {
            if (_configLoaded) return;

            lock (Sync)
            {
                if (_configLoaded) return;

                try
                {
                    var dir = Environment.GetEnvironmentVariable("STREAMDECK_TRADER_LOG_DIR");
                    _directory = !string.IsNullOrEmpty(dir)
                        ? Environment.ExpandEnvironmentVariables(dir)
                        : Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "StreamDeckTrader", "logs");

                    var level = Environment.GetEnvironmentVariable("STREAMDECK_TRADER_LOG_LEVEL");
                    if (!string.IsNullOrEmpty(level)) _fileLevel = ParseLevel(level, LevelDebug);

                    var retention = Environment.GetEnvironmentVariable("STREAMDECK_TRADER_LOG_RETENTION_DAYS");
                    int parsedRetention;
                    if (!string.IsNullOrEmpty(retention) &&
                        int.TryParse(retention, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedRetention) &&
                        parsedRetention > 0)
                    {
                        _retentionDays = parsedRetention;
                    }
                }
                catch
                {
                    _directory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "StreamDeckTrader", "logs");
                }

                _configLoaded = true;
            }
        }

        private static int ParseLevel(string value, int fallback)
        {
            switch (value.Trim().ToUpperInvariant())
            {
                case "TRACE": return LevelTrace;
                case "DEBUG": return LevelDebug;
                case "INFO":
                case "INFORMATION": return LevelInfo;
                case "WARN":
                case "WARNING": return LevelWarn;
                case "ERROR": return LevelError;
                default: return fallback;
            }
        }

        private static string LevelLabel(int level)
        {
            switch (level)
            {
                case LevelTrace: return "TRACE";
                case LevelDebug: return "DEBUG";
                case LevelInfo: return "INFO ";
                case LevelWarn: return "WARN ";
                case LevelError: return "ERROR";
                default: return "?????";
            }
        }

        /// <summary>Keeps one event on one line — embedded newlines break log parsing.</summary>
        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0) return value;
            return value.Replace("\r\n", " / ").Replace('\n', '/').Replace('\r', '/');
        }

        private static string GetProcessId()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture); }
            catch { return "?"; }
        }

        /// <summary>
        /// Version of the assembly NinjaScript compiled this add-on into — the practical way to
        /// tell whether the running code is the build that was just deployed.
        /// </summary>
        private static string GetAssemblyVersion()
        {
            try { return typeof(SdLogger).Assembly.GetName().Version.ToString(); }
            catch { return "?"; }
        }
    }
}
