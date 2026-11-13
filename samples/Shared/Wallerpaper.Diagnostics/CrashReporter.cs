using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wallerpaper.Diagnostics;

/// <summary>
/// Managed + native crash dump capture using DbgHelp MiniDumpWriteDump.
/// </summary>
public static class CrashReporter
{
    private const uint MiniDumpWithFullMemory = 0x00000002;
    private const uint MiniDumpWithHandleData = 0x00000004;
    private const uint MiniDumpWithUnloadedModules = 0x00000020;
    private const uint MiniDumpWithThreadInfo = 0x00001000;
    private const uint MiniDumpWithTokenInformation = 0x00010000;

    private static readonly object Gate = new();
    private static string _app = "Wallerpaper";
    private static int _dumping;
    private static NativeFilter? _nativeFilter;

    public static void Install(string appName)
    {
        _app = appName;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            AppLog.Error("Unhandled AppDomain exception", ex);
            WriteDump(ex, "AppDomain");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        _nativeFilter = NativeUnhandled;
        SetUnhandledExceptionFilter(_nativeFilter);
        AppLog.Info($"crash reporter installed: {appName}");
    }

    public static void WriteDump(Exception? exception, string reason)
    {
        if (Interlocked.Exchange(ref _dumping, 1) == 1)
            return;

        try
        {
            var dir = AppLog.DumpDirectory;
            Directory.CreateDirectory(dir);
            var dump = Path.Combine(dir,
                $"{_app}-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.dmp");

            using var fs = new FileStream(dump, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var info = new MINIDUMP_EXCEPTION_INFORMATION
            {
                ThreadId = GetCurrentThreadId(),
                ExceptionPointers = Marshal.GetExceptionPointers(),
                ClientPointers = false
            };
            var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MINIDUMP_EXCEPTION_INFORMATION>());
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                var ok = MiniDumpWriteDump(
                    GetCurrentProcess(),
                    (uint)Environment.ProcessId,
                    fs.SafeFileHandle.DangerousGetHandle(),
                    MiniDumpWithFullMemory | MiniDumpWithHandleData |
                    MiniDumpWithUnloadedModules | MiniDumpWithThreadInfo |
                    MiniDumpWithTokenInformation,
                    infoPtr,
                    IntPtr.Zero,
                    IntPtr.Zero);
                if (ok)
                    AppLog.Error($"dump written: {dump} ({reason})", exception);
                else
                    AppLog.Error($"MiniDumpWriteDump failed: {Marshal.GetLastWin32Error()}", exception);
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("dump creation failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _dumping, 0);
        }
    }

    private static bool NativeUnhandled(IntPtr exceptionPointers)
    {
        try
        {
            if (Interlocked.Exchange(ref _dumping, 1) == 1)
                return false;

            var dir = AppLog.DumpDirectory;
            Directory.CreateDirectory(dir);
            var dump = Path.Combine(dir,
                $"{_app}-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-native.dmp");
            using var fs = new FileStream(dump, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var info = new MINIDUMP_EXCEPTION_INFORMATION
            {
                ThreadId = GetCurrentThreadId(),
                ExceptionPointers = exceptionPointers,
                ClientPointers = false
            };
            var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MINIDUMP_EXCEPTION_INFORMATION>());
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                MiniDumpWriteDump(GetCurrentProcess(), (uint)Environment.ProcessId,
                    fs.SafeFileHandle.DangerousGetHandle(),
                    MiniDumpWithFullMemory | MiniDumpWithHandleData |
                    MiniDumpWithUnloadedModules | MiniDumpWithThreadInfo,
                    infoPtr, IntPtr.Zero, IntPtr.Zero);
                AppLog.Error($"native dump written: {dump}");
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
                Interlocked.Exchange(ref _dumping, 0);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("native dump failed", ex);
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINIDUMP_EXCEPTION_INFORMATION
    {
        public uint ThreadId;
        public IntPtr ExceptionPointers;
        [MarshalAs(UnmanagedType.Bool)]
        public bool ClientPointers;
    }

    private delegate bool NativeFilter(IntPtr exceptionPointers);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file,
        uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(NativeFilter filter);
}
