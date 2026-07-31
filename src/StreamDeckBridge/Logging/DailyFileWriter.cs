using System.Text;

namespace StreamDeckBridge.Logging;

/// <summary>
/// Append-only writer that keeps one log file per calendar day.
///
/// The three components (plugin, bridge, NT8 add-on) all write into the same directory with
/// the same line format, so a whole session can be replayed by sorting the three files of a
/// given day together.
///
/// Writes are flushed immediately: a bridge that dies mid-order must leave the lines that
/// explain why on disk, which a buffered writer would not.
/// </summary>
public sealed class DailyFileWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _prefix;
    private readonly int _retentionDays;
    private readonly long _maxBytes;

    private StreamWriter? _writer;
    private DateOnly _openDate;
    private int _rollIndex;
    private bool _disposed;

    public DailyFileWriter(string directory, string prefix, int retentionDays, int maxFileSizeMb)
    {
        _directory = directory;
        _prefix = prefix;
        _retentionDays = Math.Max(1, retentionDays);
        _maxBytes = Math.Max(1, maxFileSizeMb) * 1024L * 1024L;
    }

    /// <summary>Path of the file currently being written to, or "" before the first write.</summary>
    public string CurrentPath { get; private set; } = "";

    public void Write(string line)
    {
        if (_disposed) return;

        lock (_sync)
        {
            try
            {
                EnsureWriter();
                _writer!.WriteLine(line);
            }
            catch
            {
                // Logging must never take the bridge down. Drop the line and retry on the next
                // call with a fresh handle (the file may have been moved or the disk gone).
                DisposeWriter();
            }
        }
    }

    private void EnsureWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (_writer != null && today != _openDate)
        {
            DisposeWriter();
            _rollIndex = 0;
        }

        // A single day exceeding the size cap keeps its date but gains a .1, .2… suffix, so a
        // runaway loop cannot fill the disk with one unopenable file.
        if (_writer != null && _writer.BaseStream.Length >= _maxBytes)
        {
            DisposeWriter();
            _rollIndex++;
        }

        if (_writer != null) return;

        Directory.CreateDirectory(_directory);
        _openDate = today;

        var name = _rollIndex == 0
            ? $"{_prefix}-{today:yyyy-MM-dd}.log"
            : $"{_prefix}-{today:yyyy-MM-dd}.{_rollIndex}.log";

        CurrentPath = Path.Combine(_directory, name);

        // UTF-8 with a BOM on a freshly created file (StreamWriter skips the preamble when
        // appending to an existing one): the logs carry accents and em-dashes, and without the
        // BOM Notepad and PowerShell 5.1 read them as ANSI and show mojibake.
        _writer = new StreamWriter(
            new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(true))
        {
            AutoFlush = true
        };

        PurgeOldFiles();
    }

    /// <summary>Deletes this component's log files older than the retention window.</summary>
    private void PurgeOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
            foreach (var path in Directory.EnumerateFiles(_directory, $"{_prefix}-*.log"))
            {
                if (string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.GetLastWriteTime(path) >= cutoff) continue;
                try { File.Delete(path); } catch { /* file in use — try again tomorrow */ }
            }
        }
        catch
        {
            // Retention is best-effort; never block logging on it.
        }
    }

    private void DisposeWriter()
    {
        try { _writer?.Dispose(); } catch { /* already gone */ }
        _writer = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            DisposeWriter();
        }
    }
}
