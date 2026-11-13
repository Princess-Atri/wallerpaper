using Microsoft.UI.Xaml;
using System.Threading;
using Wallerpaper.Diagnostics;

namespace Wallerpaper.Shell;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    private static Mutex? _singleInstance;
    private static bool _ownsSingleInstance;
    private static EventWaitHandle? _showEvent;
    private bool _signalExisting;

    public App()
    {
        InitializeComponent();
        AppLog.Initialize("WallerpaperShell");
        CrashReporter.Install("WallerpaperShell");
        AppLog.Info($"WallerpaperShell starting {Environment.ProcessPath}");

        _singleInstance = new Mutex(true, @"Local\WallerpaperShell", out var createdNew);
        if (!createdNew)
        {
            try
            {
                _ownsSingleInstance = _singleInstance.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                _ownsSingleInstance = true;
            }
        }
        else
        {
            _ownsSingleInstance = true;
        }
        if (!_ownsSingleInstance)
        {
            AppLog.Info("another WallerpaperShell instance already owns the mutex");
            try
            {
                _showEvent = EventWaitHandle.OpenExisting(@"Local\WallerpaperShell.Show");
                _signalExisting = _showEvent.Set();
            }
            catch
            {
                // Older instance without the activation event.
            }
        }
        else
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                @"Local\WallerpaperShell.Show");
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showEvent.WaitOne();
                        MainWindow?.DispatcherQueue.TryEnqueue(
                            () => MainWindow?.ActivateFromSecondInstance());
                    }
                    catch { }
                }
            })
            {
                IsBackground = true,
                Name = "WallerpaperShell-activation"
            };
            thread.Start();
        }

        UnhandledException += (_, e) =>
        {
            AppLog.Error("WinUI unhandled exception", e.Exception);
            CrashReporter.WriteDump(e.Exception, "WinUI");
            // A deletion/refresh exception must not take down the whole
            // wallpaper manager. Keep the UI alive and leave a log entry.
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!_ownsSingleInstance)
        {
            Exit();
            return;
        }
        var minimized = Environment.GetCommandLineArgs()
            .Any(x => x.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        AppLog.Info($"OnLaunched minimized={minimized}");
        MainWindow = new MainWindow(minimized);
        MainWindow.Activate();
    }
}
