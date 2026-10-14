using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace RedmiBudsTray;

public partial class MainWindow
{
    private bool _renderingVolume, _volumeReading, _volumeWriting, _volumeMutating, _volumeMuted;
    private ulong? _volumeAddress;
    private (ulong Address, float Level)? _pendingVolume;
    private DispatcherTimer? _volumeWriteTimer;
    private bool DemoVolume => Environment.GetCommandLineArgs().Contains("--demo");
    private bool _volumeSurfaceDragging;

    private bool IsVolumeSurfaceSource(DependencyObject? source)
    {
        while (source is not null)
        {
            // Preserve the mute button and the Thumb's native drag behaviour.
            if (ReferenceEquals(source, VolumeMuteButton) || source is Thumb) return false;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return true;
    }

    private double VolumeAtPanelPoint(Point point)
    {
        VolumeSlider.ApplyTemplate();
        var track = (Track)VolumeSlider.Template.FindName("PART_Track", VolumeSlider);
        var position = VolumePanel.TranslatePoint(point, track);
        var thumbWidth = track.Thumb?.ActualWidth ?? 0;
        var travel = Math.Max(1, track.ActualWidth - thumbWidth);
        var fraction = Math.Clamp((position.X - thumbWidth / 2) / travel, 0, 1);
        if (track.IsDirectionReversed) fraction = 1 - fraction;
        return Math.Round(VolumeSlider.Minimum + fraction * (VolumeSlider.Maximum - VolumeSlider.Minimum));
    }

    private bool BeginVolumeSurfaceInteraction(Point point)
    {
        if (!VolumeSlider.IsEnabled || !VolumePanel.IsVisible || _exitRequested) return false;
        VolumeSlider.Focus();
        if (!System.Windows.Input.Mouse.Capture(VolumeSlider, CaptureMode.Element)) return false;
        _volumeSurfaceDragging = true;
        VolumeSlider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, VolumeAtPanelPoint(point));
        return true;
    }

    private void ReleaseVolumeSurfaceCapture()
    {
        if (!_volumeSurfaceDragging) return;
        _volumeSurfaceDragging = false;
        if (ReferenceEquals(System.Windows.Input.Mouse.Captured, VolumeSlider)) System.Windows.Input.Mouse.Capture(null);
    }

    private void Volume_SurfaceDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsVolumeSurfaceSource(e.OriginalSource as DependencyObject)) return;
        e.Handled = BeginVolumeSurfaceInteraction(e.GetPosition(VolumePanel));
    }

    private void Volume_SurfaceMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_volumeSurfaceDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { ReleaseVolumeSurfaceCapture(); return; }
        VolumeSlider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, VolumeAtPanelPoint(e.GetPosition(VolumePanel)));
        e.Handled = true;
    }

    private void Volume_SurfaceUp(object sender, MouseButtonEventArgs e)
    {
        if (!_volumeSurfaceDragging) return;
        VolumeSlider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, VolumeAtPanelPoint(e.GetPosition(VolumePanel)));
        ReleaseVolumeSurfaceCapture();
        e.Handled = true;
    }

    private void Volume_SurfaceLostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, VolumeSlider)) _volumeSurfaceDragging = false;
    }

    private void RenderVolume(BudsState state)
    {
        VolumePanel.Visibility = state.BluetoothConnected || state.Connected ? Visibility.Visible : Visibility.Collapsed;
        if (VolumePanel.Visibility != Visibility.Visible) ReleaseVolumeSurfaceCapture();
        if (DemoVolume) { ApplyVolume(new(0.42f, false)); return; }
        if (_volumeAddress != _controller.DeviceAddress)
        {
            _volumeAddress = _controller.DeviceAddress;
            _pendingVolume = null;
            _volumeWriteTimer?.Stop();
            VolumeSlider.IsEnabled = VolumeMuteButton.IsEnabled = false;
            VolumeValueText.Text = "—";
        }
        _ = UpdateVolumeAsync();
    }

    private async Task UpdateVolumeAsync()
    {
        if (DemoVolume || !IsVisible || _exitRequested || _volumeReading || _volumeWriting || _volumeMutating || _pendingVolume.HasValue || VolumeSlider.IsMouseCaptureWithin || VolumePanel.Visibility != Visibility.Visible || _controller.DeviceAddress is not ulong address) return;
        _volumeReading = true;
        try
        {
            var volume = await Task.Run(() => BluetoothAudioConnection.GetVolume(address));
            if (!_exitRequested && address == _controller.DeviceAddress && !_volumeWriting && !_volumeMutating && !_pendingVolume.HasValue && !VolumeSlider.IsMouseCaptureWithin)
                ApplyVolume(volume);
        }
        catch (Exception ex)
        {
            if (address == _controller.DeviceAddress && !_exitRequested)
            {
                VolumeSlider.IsEnabled = VolumeMuteButton.IsEnabled = false;
                VolumeValueText.Text = "—";
                VolumePanel.ToolTip = ex.Message;
            }
        }
        finally
        {
            _volumeReading = false;
            if (!_exitRequested && address != _controller.DeviceAddress) _ = UpdateVolumeAsync();
        }
    }

    private void ApplyVolume(BluetoothAudioConnection.VolumeState volume)
    {
        _renderingVolume = true;
        try { VolumeSlider.Value = Math.Round(volume.Level * 100); }
        finally { _renderingVolume = false; }
        _volumeMuted = volume.Muted;
        VolumeValueText.Text = ((int)Math.Round(volume.Level * 100)).ToString();
        VolumeMuteButton.Content = volume.Muted || volume.Level == 0 ? "\uE74F" : "\uE767";
        VolumeMuteButton.ToolTip = volume.Muted ? "取消静音" : "静音";
        VolumePanel.ToolTip = null;
        VolumeSlider.IsEnabled = VolumeMuteButton.IsEnabled = true;
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_renderingVolume || !IsInitialized) return;
        VolumeValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
        if (DemoVolume || !VolumeSlider.IsEnabled || _controller.DeviceAddress is not ulong address || address != _volumeAddress) return;
        _pendingVolume = (address, (float)(e.NewValue / 100));
        if (_volumeWriteTimer is null)
        {
            _volumeWriteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _volumeWriteTimer.Tick += (_, _) => { _volumeWriteTimer.Stop(); _ = FlushVolumeAsync(); };
        }
        _volumeWriteTimer.Stop(); _volumeWriteTimer.Start();
    }

    private async Task FlushVolumeAsync()
    {
        if (_volumeWriting || _exitRequested || _pendingVolume is not { } pending) return;
        _pendingVolume = null;
        if (pending.Address != _controller.DeviceAddress) return;
        _volumeWriting = true;
        try
        {
            await Task.Run(() => { if (!_exitRequested && pending.Address == _controller.DeviceAddress) BluetoothAudioConnection.SetVolume(pending.Address, pending.Level); });
        }
        catch (Exception ex) { if (!_exitRequested) VolumePanel.ToolTip = ex.Message; }
        finally
        {
            _volumeWriting = false;
            if (!_exitRequested)
            {
                if (_pendingVolume.HasValue) _volumeWriteTimer?.Start();
                else _ = UpdateVolumeAsync();
            }
        }
    }

    private async void VolumeMute_Click(object sender, RoutedEventArgs e)
    {
        if (DemoVolume) { ApplyVolume(new((float)(VolumeSlider.Value / 100), !_volumeMuted)); return; }
        if (_volumeMutating || _controller.DeviceAddress is not ulong address) return;
        _volumeMutating = true;
        VolumeMuteButton.IsEnabled = false;
        var muted = !_volumeMuted;
        try { await Task.Run(() => { if (address == _controller.DeviceAddress && !_exitRequested) BluetoothAudioConnection.SetMuted(address, muted); }); }
        catch (Exception ex) { if (!_exitRequested) VolumePanel.ToolTip = ex.Message; }
        finally { _volumeMutating = false; if (!_exitRequested) _ = UpdateVolumeAsync(); }
    }
}
