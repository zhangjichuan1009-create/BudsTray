using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RedmiBudsTray;

public partial class MainWindow
{
    private DispatcherTimer? _dismissTimer;
    private bool _outsideButtonDown;
    private bool _outsidePress;
    private long _pointerReleaseDeadline;
    private long _shownAt, _transitionDeadline;
    private NativeRect _openDestination;

    private void WatchOutsideClicks()
    {
        _shownAt = Environment.TickCount64;
        _outsideButtonDown = MouseButtonDown();
        _outsidePress = false;
        _pointerReleaseDeadline = 0;
        _dismissTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _dismissTimer.Tick -= CheckOutsideClick;
        _dismissTimer.Tick += CheckOutsideClick;
        _dismissTimer.Start();
    }

    private void CheckOutsideClick(object? sender, EventArgs e)
    {
        if (_exitRequested || _popup.Phase is PopupPhase.Hidden or PopupPhase.Menu or PopupPhase.Exited) { _dismissTimer?.Stop(); return; }
        if (_hiding)
        {
            if (Environment.TickCount64 >= _transitionDeadline) FinishHiding(_popup.Revision);
            return;
        }
        if (_showing && Environment.TickCount64 >= _transitionDeadline) FinishOpening(_popup.Revision);
        if (Environment.GetCommandLineArgs().Any(arg => arg.StartsWith("--preview="))) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var rect)) return;
        var cursor = System.Windows.Forms.Cursor.Position;
        ProcessDismissal(MouseButtonDown(), GetForegroundWindow() == hwnd,
            cursor.X < rect.Left || cursor.X >= rect.Right || cursor.Y < rect.Top || cursor.Y >= rect.Bottom,
            Environment.TickCount64);
    }

    private void ProcessDismissal(bool down, bool foreground, bool outside, long now)
    {
        var pressed = down && !_outsideButtonDown;
        _outsideButtonDown = down;
        if (_hiding) return;
        if (IsMouseCaptureWithin)
        {
            // A captured Thumb keeps its gesture even when it moves beyond the window.
            _outsidePress = false;
            _pointerReleaseDeadline = now + 180;
            return;
        }
        if (pressed) _outsidePress = outside;
        if (down)
        {
            // Let the shell deliver MouseClick before acting on focus loss.
            _pointerReleaseDeadline = now + 180;
            return;
        }
        if (now < _pointerReleaseDeadline) return;
        if (now - _shownAt > 300 && (!foreground || _outsidePress))
            HideAnimated();
        _outsidePress = false;
    }

    private void FinishOpening(int revision)
    {
        if (!_popup.Complete(revision, PopupPhase.Opening, PopupPhase.Open)) return;
        StopSlide();
        MoveNative(_openDestination);
        Render(_controller.State);
        if (!_exitRequested) _ = _controller.RefreshAsync(force: true);
    }

    private void FinishHiding(int revision)
    {
        if (!_popup.Complete(revision, PopupPhase.Closing, PopupPhase.Hidden)) return;
        SettleHidden(PopupPhase.Hidden);
    }

    private void SettleHidden(PopupPhase next)
    {
        ReleaseVolumeSurfaceCapture();
        CloseDevicePicker(animated: false);
        SettleOverlay(SettingsPage, SettingsContent, _settingsMotion);
        SettleOverlay(RedmiDevicePage, RedmiDeviceContent, _redmiMotion);
        SettleOverlay(DevicesPage, DevicesContent, _devicesMotion);
        SettleOverlay(DeviceConfigPage, DeviceConfigContent, _configMotion);
        CancelPanelResize(settle: true);
        _popup.Begin(next);
        _dismissTimer?.Stop();
        StopSlide();
        _pendingState = null;
        _controller.PanelVisible = false;
        if (IsVisible) Hide();
    }

    private static bool MouseButtonDown() => (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
