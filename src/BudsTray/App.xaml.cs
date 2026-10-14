using System.Configuration;
using System.Data;
using System.Windows;

namespace RedmiBudsTray;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private MainWindow? _window;
    private Mutex? _singleInstance;
    private EventWaitHandle? _shutdownSignal;
    private RegisteredWaitHandle? _shutdownWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--shutdown"))
        {
            try { using var signal = EventWaitHandle.OpenExisting(@"Local\RedmiBudsTray.Shutdown"); signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        if (e.Args.Contains("--uninstall-cleanup"))
        {
            try { AutoStartService.RemoveIfOwned(); Shutdown(0); }
            catch { Shutdown(1); }
            return;
        }
        DispatcherUnhandledException += (_, args) => AppLog.Write("Unhandled UI exception: " + args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Write("Unhandled exception: " + args.ExceptionObject);
        AppLog.Write($"App startup; autostart={e.Args.Contains("--autostart")}; paused={ConnectionPreferences.Paused}");
        if (e.Args.Contains("--repair-autostart"))
        {
            try { AutoStartService.SetEnabled(true); Shutdown(0); }
            catch (Exception ex) { AppLog.Write("Startup repair failed: " + ex); Shutdown(1); }
            return;
        }
        if (!e.Args.Contains("--demo"))
        {
            _singleInstance = new Mutex(true, @"Local\RedmiBudsTray.SingleInstance", out var created);
            if (!created) { _singleInstance.Dispose(); _singleInstance = null; Shutdown(); return; }
            _shutdownSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\RedmiBudsTray.Shutdown");
            _shutdownWait = ThreadPool.RegisterWaitForSingleObject(_shutdownSignal,
                (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, Timeout.Infinite, true);
        }
        if (!e.Args.Contains("--demo")) PanelDeviceStore.Initialize(BluetoothDeviceFinder.FindAll(), ConnectionPreferences.SelectedAddress);
        _window = new MainWindow();
        _window.InitializeTray();
        if (!e.Args.Contains("--demo"))
        {
            try { AutoStartService.RepairIfEnabled(); }
            catch (Exception ex) { AppLog.Write("Startup registration check failed: " + ex.Message); }
        }
        var preview = e.Args.FirstOrDefault(x => x.StartsWith("--preview=", StringComparison.OrdinalIgnoreCase));
        if (preview is not null)
        {
            // Inspection tools omit owned tray/tool windows. Expose only the
            // explicitly requested demo preview; production remains tray-only.
            if (e.Args.Contains("--demo") && e.Args.Contains("--preview-inspect")) _window.ShowInTaskbar = true;
            _window.ShowForPreview();
            var delay = e.Args.Any(x => x.Equals("--demo", StringComparison.OrdinalIgnoreCase)) ? 900 : 7000;
            var previewDelay = e.Args.FirstOrDefault(x => x.StartsWith("--preview-delay-ms="));
            if (e.Args.Contains("--demo") && previewDelay is not null && int.TryParse(previewDelay[19..], out var milliseconds))
                delay = Math.Clamp(milliseconds, 900, 60000);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _window.SavePreview(preview["--preview=".Length..]);
                Shutdown();
            };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdownWait?.Unregister(null); _shutdownSignal?.Dispose();
        _singleInstance?.ReleaseMutex(); _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

