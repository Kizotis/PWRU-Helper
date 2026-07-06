using System.IO;
using System.Text;

namespace PWRUHelper.Services;

/// <summary>
/// A tiny, best-effort application log. Timestamped lines go to
/// %AppData%\PWRUHelper\logs\log.txt; the file rolls over to log.1.txt once it passes a size
/// cap, so the log can never grow without bound (at most two files ≈ 2× the cap).
///
/// The real mechanics live in <see cref="LogWriter"/> (which takes an explicit directory so
/// it can be unit-tested); this static facade just wires a single writer to %AppData% and is
/// what the rest of the app calls. Every method swallows its own errors on purpose — logging
/// must never be the thing that crashes the app or blocks a feature.
/// </summary>
public static class Logging
{
    private static readonly LogWriter Default = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PWRUHelper", "logs"));

    public static void Info(string message) => Default.Write("INFO", message);
    public static void Warn(string message) => Default.Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Default.Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex}");

    /// <summary>Recent log text for a copy-to-clipboard error report ("" if nothing/failed).</summary>
    public static string ReadRecent(int maxChars = 30_000) => Default.ReadRecent(maxChars);
}

/// <summary>
/// The rolling-file mechanics behind <see cref="Logging"/>. Writes timestamped lines to
/// log.txt in <paramref name="dir"/> and rolls to log.1.txt once the active file passes
/// <paramref name="maxBytes"/>, keeping exactly two files so the log is bounded at ~2× the
/// cap. Thread-safe and never throws — a read-only disk just means no diagnostics.
/// </summary>
internal sealed class LogWriter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _dir;
    private readonly string _logPath;
    private readonly string _prevPath;
    private readonly long _maxBytes;

    public LogWriter(string dir, long maxBytes = 1024 * 1024)   // 1 MB default cap
    {
        _dir = dir;
        _logPath = Path.Combine(dir, "log.txt");
        _prevPath = Path.Combine(dir, "log.1.txt");
        _maxBytes = maxBytes;
    }

    public void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_dir);
                RollIfTooBig();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(_logPath, line, Utf8NoBom);
            }
        }
        catch { /* best-effort: never throw from logging */ }
    }

    // Keep exactly one previous file: when the active log passes the cap, the old .1 is
    // discarded and the current log becomes the new .1 (a fresh log.txt is created on the
    // next write). A failed roll is harmless — we just keep appending to the current file.
    private void RollIfTooBig()
    {
        try
        {
            var fi = new FileInfo(_logPath);
            if (!fi.Exists || fi.Length < _maxBytes) return;
            if (File.Exists(_prevPath)) File.Delete(_prevPath);
            File.Move(_logPath, _prevPath);
        }
        catch { /* if the roll fails, keep using the current file */ }
    }

    public string ReadRecent(int maxChars = 30_000)
    {
        try
        {
            lock (_gate)
            {
                var sb = new StringBuilder();
                foreach (var path in new[] { _prevPath, _logPath })   // older first → oldest to newest
                    if (File.Exists(path)) sb.Append(File.ReadAllText(path, Utf8NoBom));

                var all = sb.ToString();
                if (all.Length <= maxChars) return all;

                // Trim to a whole line so the report doesn't start mid-sentence.
                var tail = all[^maxChars..];
                int nl = tail.IndexOf('\n');
                return (nl >= 0 && nl < tail.Length - 1) ? tail[(nl + 1)..] : tail;
            }
        }
        catch { return ""; }
    }
}
