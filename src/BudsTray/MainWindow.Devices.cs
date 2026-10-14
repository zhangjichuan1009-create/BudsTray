using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace RedmiBudsTray;

public partial class MainWindow
{
    private int _pickerRevision;
    private bool _switchingDevice;
    private int _pairedDeviceCount;
    private void UpdateDeviceAvailability(int count)
    {
        _pairedDeviceCount = count;
        DevicePickerChevron.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        DevicePickerButton.IsEnabled = !_switchingDevice;
        DevicePickerButton.IsHitTestVisible = count > 1;
        DevicePickerButton.Focusable = count > 1;
        if (count <= 1 && _pickerMotion.Open) CloseDevicePicker(animated: false);
    }
    private void CloseDevicePicker(bool animated = true)
    {
        _pickerRevision++;
        AnimateOverlay(DevicePicker, DevicePicker, _pickerMotion, false, true, animated);
    }

    private async void DevicePicker_Click(object sender, RoutedEventArgs e)
    {
        if (_switchingDevice) return;
        if (_pickerMotion.Open) { CloseDevicePicker(); return; }
        var revision = ++_pickerRevision;
        try
        {
            var candidates = PanelDeviceStore.Devices();
            // Reserve the actual rows before the entrance starts, avoiding a height
            // jump when the slower audio-status lookup completes.
            FillDevicePicker(candidates.Select(device => (device, false)), pending: true);
            AnimateOverlay(DevicePicker, DevicePicker, _pickerMotion, true, true);
            var devices = await Task.Run(() => candidates.Select(device =>
                (Device: device, Connected: BluetoothAudioConnection.IsAudioReady(device.Address))).ToArray());
            if (revision != _pickerRevision || _exitRequested) return;
            FillDevicePicker(devices);
        }
        catch (Exception ex)
        {
            if (revision != _pickerRevision) return;
            DevicePickerItems.Children.Clear();
            DevicePickerItems.Children.Add(new TextBlock { Text = "暂时无法读取设备", ToolTip = ex.Message, FontSize = 14, TextAlignment = TextAlignment.Center, Margin = new Thickness(12) });
            AnimateOverlay(DevicePicker, DevicePicker, _pickerMotion, true, true);
        }
    }

    private void FillDevicePicker(IEnumerable<(PairedBluetoothDevice Device, bool Connected)> devices, bool pending = false)
    {
        DevicePickerItems.Children.Clear();
        foreach (var (device, connected) in devices)
        {
            var selected = _controller.DeviceAddress == device.Address;
            var knownConnected = pending && selected && (_controller.State.BluetoothConnected || _controller.State.Connected);
            var isConnected = connected || knownConnected;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = device.Name, FontSize = 14, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock { Text = pending && !knownConnected ? "—" : isConnected ? "已连接" : "未连接", FontSize = 13, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Resources[isConnected ? "Accent" : "TextSecondary"] };
            Grid.SetColumn(status, 1); grid.Children.Add(name); grid.Children.Add(status);
            var button = new Button { Style = (Style)Resources["DeviceButton"], Height = 48, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 2, 0, 2), Content = grid, Background = selected ? (Brush)Resources["AccentSoft"] : Brushes.Transparent };
            button.Click += async (_, _) => await SwitchPanelDeviceAsync(device.Address);
            DevicePickerItems.Children.Add(button);
        }
        if (DevicePickerItems.Children.Count == 0)
            DevicePickerItems.Children.Add(new TextBlock { Text = "没有已配对的受支持耳机", FontSize = 14, TextAlignment = TextAlignment.Center, Margin = new Thickness(12) });
    }

    private async Task SwitchPanelDeviceAsync(ulong address)
    {
        if (_switchingDevice) return;
        CloseDevicePicker();
        _switchingDevice = true;
        _userConnecting = true;
        DevicePickerButton.IsEnabled = false;
        try { await _controller.SelectDeviceAsync(address); }
        catch (Exception ex) { if (!_exitRequested) System.Windows.MessageBox.Show(this, ex.Message, "设备切换未完成", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally
        {
            _switchingDevice = false;
            _userConnecting = false;
            if (!_exitRequested) { DevicePickerButton.IsEnabled = true; Render(_controller.State); }
        }
    }
}
