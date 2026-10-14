using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RedmiBudsTray;

public partial class MainWindow
{
    private sealed class OverlayMotion { public bool Open; public int Revision; public bool Running; }
    private readonly OverlayMotion _pickerMotion = new(), _settingsMotion = new();

    private void AnimateOverlay(FrameworkElement surface, FrameworkElement content, OverlayMotion motion, bool show, bool vertical, bool animate = true)
    {
        if (motion.Open == show && motion.Running && animate) return;
        var wasVisible = surface.Visibility == Visibility.Visible;
        var currentOpacity = wasVisible ? content.Opacity : 0;
        var translation = content.RenderTransform as TranslateTransform ?? new TranslateTransform();
        content.RenderTransform = translation;
        var positionProperty = vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        var currentPosition = wasVisible ? (double)translation.GetValue(positionProperty) : vertical ? -8 : 18;
        var revision = ++motion.Revision;
        motion.Open = show;
        motion.Running = false;
        content.BeginAnimation(OpacityProperty, null);
        translation.BeginAnimation(positionProperty, null);
        if (!animate || !SystemParameters.ClientAreaAnimation || (!show && !wasVisible))
        {
            content.Opacity = 1;
            translation.X = translation.Y = 0;
            surface.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            surface.IsHitTestVisible = show;
            return;
        }
        surface.Visibility = Visibility.Visible;
        surface.IsHitTestVisible = show || !vertical;
        // Keep the base value at the displayed position until the new animation
        // clock receives its first tick, including immediate back/open reversals.
        content.Opacity = currentOpacity;
        translation.SetValue(positionProperty, currentPosition);
        motion.Running = true;
        var duration = TimeSpan.FromMilliseconds(show ? (vertical ? 160 : 200) : (vertical ? 110 : 140));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacity = new DoubleAnimation(currentOpacity, show ? 1 : 0, duration) { EasingFunction = ease };
        var move = new DoubleAnimation(currentPosition, show ? 0 : vertical ? -6 : 14, duration) { EasingFunction = ease };
        opacity.Completed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (_exitRequested || revision != motion.Revision) return;
            SettleOverlay(surface, content, motion);
            if (ReferenceEquals(surface, SettingsPage) && !motion.Open)
            {
                UpdateLayout();
                AnimateModeIndicator(_presentedNoiseMode, false);
                foreach (var element in new FrameworkElement[] { DeviceHeader, BatteryPanel, HeadphonePanel, SoundPanel, VolumePanel, DisconnectedPanel })
                    if (element.Visibility == Visibility.Visible)
                        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
            }
        }));
        content.BeginAnimation(OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
        translation.BeginAnimation(positionProperty, move, HandoffBehavior.SnapshotAndReplace);
    }

    private static void SettleOverlay(FrameworkElement surface, FrameworkElement content, OverlayMotion motion)
    {
        ++motion.Revision;
        motion.Running = false;
        content.BeginAnimation(OpacityProperty, null);
        content.Opacity = 1;
        if (content.RenderTransform is TranslateTransform translation)
        {
            translation.BeginAnimation(TranslateTransform.XProperty, null);
            translation.BeginAnimation(TranslateTransform.YProperty, null);
            translation.X = translation.Y = 0;
        }
        surface.Visibility = motion.Open ? Visibility.Visible : Visibility.Collapsed;
        surface.IsHitTestVisible = motion.Open;
    }

    private void ShowSettings()
    {
        _configMotion.Open = false;
        SettleOverlay(DeviceConfigPage, DeviceConfigContent, _configMotion);
        _devicesMotion.Open = false;
        SettleOverlay(DevicesPage, DevicesContent, _devicesMotion);
        CloseDevicePicker();
        CloseRedmiSettings(animated: false);
        AnimateOverlay(SettingsPage, SettingsContent, _settingsMotion, true, false);
    }
}
