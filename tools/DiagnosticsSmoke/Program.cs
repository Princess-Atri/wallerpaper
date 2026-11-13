using Wallerpaper.Diagnostics;

var root = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "diag-smoke");
var logs = Path.Combine(root, "logs");

AppLog.Initialize("Smoke", logs);
AppLog.Info("diagnostics smoke start");
// Exercise the exact same path used by a crash handler.
CrashReporter.WriteDump(new InvalidOperationException("diagnostics smoke"), "smoke");
AppLog.Error("diagnostics smoke completed");

Console.WriteLine($"logs={logs}");
Console.WriteLine($"dumps={AppLog.DumpDirectory}");
Console.WriteLine($"log={string.Join(",", Directory.GetFiles(logs))}");
Console.WriteLine($"dump={string.Join(",", Directory.GetFiles(AppLog.DumpDirectory))}");
