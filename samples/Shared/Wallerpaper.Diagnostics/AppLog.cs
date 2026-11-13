using System.Text;

namespace Wallerpaper.Diagnostics;

/// <summary>
/// Small, dependency-free, process-local file logger.
/// %LOCALAPPDATA%\Wallerpaper\logs\<process>-yyyyMMdd.log
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string _app = "Wallerpaper";
    private static string _directory = "";
    private static string _file = "";
    private static long _maxBytes = 10 * 1024 * 1024;

    public static string Directory => _directory;
    public static string DumpDirectory =>
        Path.Combine(Path.GetDirectoryName(_directory) ?? _directory, "dumps");

    public static void Initialize(string appName, string? directory = null)
    {
        lock (Gate)
        {
            _app = Sanitize(appName);
            _directory = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wallerpaper", "logs");

            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                System.IO.Directory.CreateDirectory(DumpDirectory);
            }
            catch
            {
                // Low-integrity/read-only environments: fall back to temp.
                try
                {
                    _directory = Path.Combine(Path.GetTempPath(), "Wallerpaper", "logs");
                    System.IO.Directory.CreateDirectory(_directory);
                    System.IO.Directory.CreateDirectory(DumpDirectory);
                }
                catch
                {
                    _directory = "";
                }
            }

            _file = string.IsNullOrEmpty(_directory)
                ? ""
                : Path.Combine(_directory, $"{_app}-{DateTime.Now:yyyyMMdd}.log");
            CleanupOldLogs();
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public static void Write(string level, string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                if (string.IsNullOrEmpty(_file))
                    Initialize(_app);

                RotateIfNeeded();
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(level).Append("] [").Append(Environment.ProcessId).Append("] ")
                    .Append(message)
                    .AppendLine()
                    .ToString();
                File.AppendAllText(_file, line, Encoding.UTF8);
                if (ex is not null)
                {
                    File.AppendAllText(_file, ex + Environment.NewLine, Encoding.UTF8);
                }
            }
        }
        catch
        {
            // Diagnostics must never take down the app.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_file);
            if (!info.Exists || info.Length < _maxBytes)
                return;
            var archive = _file + ".1";
            File.Delete(archive);
            File.Move(_file, archive);
        }
        catch { }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.log*"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
