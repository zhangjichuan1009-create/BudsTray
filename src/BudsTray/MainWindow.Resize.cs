using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace RedmiBudsTray;

public partial class MainWindow
{
    private static readonly DependencyProperty LayoutProgressProperty = DependencyProperty.Register(
        "LayoutProgress", typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (owner, args) => ((MainWindow)owner).ApplyPanelLayout((double)args.NewValue)));
    private double _layoutTarget = double.NaN;
    private bool _resizeActive, _resizeDrivingWindow;
    private int _resizeRevision;
    private double _resizeBottom, _resizeMaxHeight;

    private void SetPanelLayout(bool roomy)
    {
        var target = roomy ? 1.0 : 0.0;
        var current = (double)GetValue(LayoutProgressProperty);
        if (_layoutTarget == target && (_resizeActive || Math.Abs(current - target) < 0.001)) return;
        _layoutTarget = target;
        var animate = IsVisible && _popup.Phase == PopupPhase.Open && SystemParameters.ClientAreaAnimation;
        if (animate)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var monitor = MonitorFromPoint(new NativePoint { X = _anchor.X, Y = _anchor.Y }, 2);
            animate = GetWindowRect(hwnd, out var rect) && GetMonitorInfo(monitor, ref info);
            if (animate)
            {
                var scale = Math.Max(1, GetDpiForWindow(hwnd)) / 96.0;
                _resizeBottom = rect.Bottom / scale;
                _resizeMaxHeight = PopupPlacement.Calculate(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom,
                    _anchor.X, _anchor.Y, scale, 100000).Height / scale;
            }
        }
        CancelPanelResize(settle: false);
        var revision = ++_resizeRevision;
        SetValue(LayoutProgressProperty, target);
        if (!animate)
        {
            ApplyPanelLayout(target);
            if (IsVisible) PlaceOnMonitor(_anchor);
            return;
        }
        // One WPF clock drives both internal spacing and HWND geometry together.
        // Retarget from the current displayed value, rather than restarting at an endpoint.
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (revision != _resizeRevision || _exitRequested) return;
            BeginAnimation(LayoutProgressProperty, null);
            ApplyPanelLayout(target);
            _resizeActive = _resizeDrivingWindow = false;
        };
        _resizeActive = true;
        BeginAnimation(LayoutProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
        _resizeDrivingWindow = true;
        ApplyPanelLayout(current);
    }

    private void ApplyPanelLayout(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        BatteryRow.Height = new GridLength(96 + 30 * progress);
        HeaderGapRow.Height = new GridLength(14 + 6 * progress);
        ModeGapRow.Height = new GridLength(14 + 8 * progress);
        VolumeGapRow.Height = new GridLength(14 + 4 * progress);
        PanelSurface.Height = 360 + 48 * progress;
        if (!_resizeDrivingWindow || !IsVisible || _popup.Phase != PopupPhase.Open) return;
        var height = Math.Min(PanelSurface.Height, _resizeMaxHeight);
        Height = height;
        Top = _resizeBottom - height;
    }

    private void CancelPanelResize(bool settle)
    {
        ++_resizeRevision;
        var progress = settle && !double.IsNaN(_layoutTarget) ? _layoutTarget : (double)GetValue(LayoutProgressProperty);
        _resizeActive = _resizeDrivingWindow = false;
        SetValue(LayoutProgressProperty, progress);
        BeginAnimation(LayoutProgressProperty, null);
        ApplyPanelLayout(progress);
    }
}
