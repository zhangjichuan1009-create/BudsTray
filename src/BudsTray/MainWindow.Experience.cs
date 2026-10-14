using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace RedmiBudsTray;

public partial class MainWindow
{
    private bool _userConnecting, _userDisconnecting, _spinnerActive;
    private bool _darkApp, _darkTaskbar;
    private bool _lowBatteryEnabled;
    private uint _trayDpi = GetDpiForSystem();
    private LowBatteryPolicy _lowBattery = new();
    private Drawing.Point _anchor;
    private EventHandler? _slideFrame;
    private BudsState? _pendingState;

    private void InitializeExperience()
    {
        _lowBatteryEnabled = ExperienceSettings.LowBatteryEnabled;
        _lowBattery = new(ExperienceSettings.LowBatteryLatch);
        LowBatteryToggle.IsChecked = _lowBatteryEnabled;
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            StopSlide();
        };
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        => Dispatcher.BeginInvoke(() => { if (!_exitRequested) ApplyTheme(); });
    private void OnDisplaysChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() => { if (!_exitRequested) { RefreshTrayIcon(); _trayMenu?.Dismiss(); SettleHidden(PopupPhase.Hidden); } });

    private void ApplyTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        _darkApp = key?.GetValue("AppsUseLightTheme") is int app && app == 0;
        _darkTaskbar = key?.GetValue("SystemUsesLightTheme") is int system && system == 0;
        if (Environment.GetCommandLineArgs().Contains("--dark-preview")) _darkApp = _darkTaskbar = true;
        if (Environment.GetCommandLineArgs().Contains("--light-preview")) _darkApp = _darkTaskbar = false;
        SetBrush("PanelBackground", _darkApp ? "#202124" : "#F7F8FA");
        SetBrush("BackdropTint", _darkApp ? "#202124" : "#F7F8FA");
        SetBrush("CardBackground", _darkApp ? "#2B2C30" : "#FFFFFF");
        SetBrush("TextPrimary", _darkApp ? "#F4F4F5" : "#191919");
        SetBrush("TextSecondary", _darkApp ? "#C2C3C9" : "#5D5D5D");
        SetBrush("Accent", _darkApp ? "#187CD8" : "#086CC9");
        SetBrush("AccentSoft", _darkApp ? "#233C55" : "#EAF3FC");
        SetBrush("SubtleBorder", _darkApp ? "#26FFFFFF" : "#16000000");
        SetBrush("HoverBackground", _darkApp ? "#18FFFFFF" : "#0D000000");
        SetBrush("ChargingForeground", _darkApp ? "#73D99A" : "#168248");
        SetBrush("ToggleTrack", _darkApp ? "#65666E" : "#D0D0D0");
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) { var dark = _darkApp ? 1 : 0; DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); }
        RefreshTrayIcon();
        if (_trayMenu is not null)
        {
            foreach (var keyName in new[] { "PanelBackground", "TextPrimary", "TextSecondary", "SubtleBorder", "HoverBackground" })
                _trayMenu.Resources[keyName] = Resources[keyName];
            var menuHwnd = new WindowInteropHelper(_trayMenu).Handle;
            var menuDark = _darkApp ? 1 : 0;
            if (menuHwnd != 0) DwmSetWindowAttribute(menuHwnd, 20, ref menuDark, sizeof(int));
        }
    }

    private void SetBrush(string name, string hex) => Resources[name] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    private void RefreshTrayIcon()
    {
        if (_trayIcon is null) return;
        var old = _trayIcon.Icon;
        _trayIcon.Icon = CreateTrayIcon(_trayConnected ?? false, _darkTaskbar, _trayDpi);
        old?.Dispose();
    }

    private void UpdateConnectionFeedback(BudsState state)
    {
        var busy = !state.Connected && (_userConnecting || state.Status.Contains("正在连接"));
        ConnectButton.IsEnabled = DisconnectButton.IsEnabled = !_userConnecting && !_userDisconnecting;
        ConnectLabel.Text = busy ? "连接中" : _pairedDeviceCount == 0 ? "配对耳机" : "连接";
        ConnectSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy == _spinnerActive) return;
        _spinnerActive = busy;
        SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, busy
            ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    private void CheckLowBattery(BudsState state)
    {
        if (!_lowBatteryEnabled || Environment.GetCommandLineArgs().Contains("--demo")) return;
        var previous = _lowBattery.Latch;
        var warnings = _lowBattery.Evaluate(state);
        if (_lowBattery.Latch != previous)
        {
            try { ExperienceSettings.LowBatteryLatch = _lowBattery.Latch; }
            catch (Exception ex) { AppLog.Write("Battery reminder state: " + ex.Message); }
        }
        if (warnings.Length > 0)
            _trayIcon?.ShowBalloonTip(5000, state.DeviceName + " 电量偏低", string.Join(" · ", warnings), Forms.ToolTipIcon.Warning);
    }

    private void LowBattery_Click(object sender, RoutedEventArgs e)
    {
        try { ExperienceSettings.LowBatteryEnabled = LowBatteryToggle.IsChecked == true; _lowBatteryEnabled = LowBatteryToggle.IsChecked == true; }
        catch (Exception ex) { LowBatteryToggle.IsChecked = _lowBatteryEnabled; SettingsHint.Text = ex.Message; }
    }

    private NativeRect PlaceOnMonitor(Drawing.Point point)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("无法获取显示器位置");
        var dpi = GetDpiForSystem();
        if (GetDpiForMonitor(monitor, 0, out var monitorDpi, out _) == 0) dpi = monitorDpi;
        if (_trayDpi != dpi) { _trayDpi = dpi; RefreshTrayIcon(); }
        var scale = dpi / 96.0;
        if (scale <= 0) scale = 1;
        var placement = PopupPlacement.Calculate(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom, point.X, point.Y, scale, PanelSurface.Height);
        var rect = new NativeRect { Left = placement.X, Top = placement.Y, Right = placement.X + placement.Width, Bottom = placement.Y + placement.Height };
        // Let WPF own initial HWND/root-visual creation and DPI layout.
        Width = (rect.Right - rect.Left) / scale;
        Height = (rect.Bottom - rect.Top) / scale;
        Left = rect.Left / scale;
        Top = rect.Top / scale;
        return rect;
    }

    private void MoveNative(NativeRect rect)
        => SetWindowPos(new WindowInteropHelper(this).Handle, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x14);

    private void StopSlide()
    {
        if (_slideFrame is not null) CompositionTarget.Rendering -= _slideFrame;
        _slideFrame = null;
    }

    private void SlideNative(NativeRect from, NativeRect to, int duration, Action? completed = null)
    {
        StopSlide();
        if (!SystemParameters.ClientAreaAnimation)
        {
            MoveNative(to); completed?.Invoke(); return;
        }
        TimeSpan? startTime = null;
        var lastTime = TimeSpan.MinValue;
        var lastY = from.Top;
        var leaving = to.Top > from.Top;
        var hwnd = new WindowInteropHelper(this).Handle;
        _slideFrame = (_, args) =>
        {
            var now = ((RenderingEventArgs)args).RenderingTime;
            if (now == lastTime) return;
            lastTime = now;
            startTime ??= now;
            var fraction = Math.Clamp((now - startTime.Value).TotalMilliseconds / duration, 0, 1);
            var eased = leaving ? fraction * fraction : 1 - Math.Pow(1 - fraction, 3);
            var y = (int)Math.Round(from.Top + (to.Top - from.Top) * eased);
            if (y != lastY)
            {
                // Position only: do not send a size change/layout request every frame.
                SetWindowPos(hwnd, 0, to.Left, y, 0, 0, 0x415); // NOSIZE | NOZORDER | NOACTIVATE | NOSENDCHANGING
                lastY = y;
            }
            if (fraction < 1) return;
            StopSlide();
            if (completed is not null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, completed);
            if (_pendingState is not null)
            {
                _pendingState = null;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    if (!_exitRequested) Render(_controller.State);
                }));
            }
        };
        CompositionTarget.Rendering += _slideFrame;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
