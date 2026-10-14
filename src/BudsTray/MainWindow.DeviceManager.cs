using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace RedmiBudsTray;

public partial class MainWindow
{
    private readonly OverlayMotion _devicesMotion = new();
    private bool _addingDevices, _managingDevice;
    private bool _managerFromSettings;
    private string _managerStamp = "";

    private void RenderDeviceGuide(int count)
    {
        EmptyDevicesPanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceHeader.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ManageDevices_Click(object sender, RoutedEventArgs e) => ShowDeviceManager(false);
    private void AddDeviceGuide_Click(object sender, RoutedEventArgs e) => ShowDeviceManager(true);

    private void ShowDeviceManager(bool adding)
    {
        _configMotion.Open = false;
        SettleOverlay(DeviceConfigPage, DeviceConfigContent, _configMotion);
        CloseDevicePicker(animated: false);
        _managerFromSettings = _settingsMotion.Open;
        _addingDevices = adding;
        DevicesError.Visibility = Visibility.Collapsed;
        FillDeviceManager();
        AnimateOverlay(DevicesPage, DevicesContent, _devicesMotion, true, false);
    }

    private static string ProtocolLabel(EarphoneProfile? profile)
    {
        if (profile is null) return "协议待识别";
        // Observed modes are partial evidence, not a complete feature list.
        return profile.Protocol == EarphoneProtocol.Xiaomi ? "小米协议" : "万魔协议";
    }

    private void FillDeviceManager()
    {
        var paired = BluetoothDeviceFinder.FindAll();
        var managed = PanelDeviceStore.Read();
        _managerStamp = System.Text.Json.JsonSerializer.Serialize(managed);
        var items = _addingDevices
            ? paired.Where(d => managed.All(m => m.Address != d.Address)).ToArray()
            : PanelDeviceStore.Resolve(managed, paired).ToArray();
        DevicesPageTitle.Text = _addingDevices ? "添加设备" : "管理设备";
        DevicesActionButton.Content = _addingDevices ? "Windows 蓝牙设置" : "添加设备";
        FillDeviceManagerRows(items, managed, _addingDevices);
    }

    private void FillDeviceManagerRows(IReadOnlyList<PairedBluetoothDevice> items, IReadOnlyList<PanelDevice> managed, bool adding)
    {
        ManagedDeviceItems.Children.Clear();
        DevicesEmptyText.Text = adding ? "没有可添加的已配对设备" : "还没有添加设备";
        DevicesEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var device in items)
        {
            var record = managed.FirstOrDefault(d => d.Address == device.Address);
            var profile = record?.Profile ?? DeviceProtocols.Identify(device.Name);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            text.Children.Add(new TextBlock { Text = device.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = device.Name });
            var awaiting = record?.DetectionFailed == true || _controller.DeviceAddress == device.Address && _controller.State.Hint.Contains("暂未识别") ? "协议未识别" : "等待连接识别";
            text.Children.Add(new TextBlock { Text = record?.ManualProtocol is EarphoneProtocol forced ? "手动选择 · " + (forced == EarphoneProtocol.Xiaomi ? "小米协议" : "万魔协议") : !adding && record?.ProtocolConfirmed != true ? awaiting : ProtocolLabel(profile), FontSize = 12.5, Foreground = (System.Windows.Media.Brush)Resources["TextSecondary"], Margin = new Thickness(0, 5, 0, 0) });
            if (adding) row.Children.Add(text);
            else
            {
                var configure = new Button { Style = (Style)Resources["DeviceButton"], Content = text, Padding = new Thickness(0), ToolTip = "选择协议并设置降噪 / 通透按钮" };
                configure.Click += (_, _) => ShowDeviceConfig(device);
                row.Children.Add(configure);
            }
            var action = new Button { Style = (Style)Resources["IconButton"], Content = adding ? "\uE710" : "\uE74D", ToolTip = adding ? "加入面板" : "从面板移除（保留蓝牙配对）", Tag = device.Address };
            System.Windows.Automation.AutomationProperties.SetName(action, (adding ? "添加 " : "移除 ") + device.Name);
            action.Click += async (_, _) => await ChangePanelDeviceAsync(device, adding);
            Grid.SetColumn(action, 1); row.Children.Add(action);
            ManagedDeviceItems.Children.Add(new Border { Style = (Style)Resources["CardStyle"], Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), Child = row });
        }
    }

    private async Task ChangePanelDeviceAsync(PairedBluetoothDevice device, bool adding)
    {
        if (_managingDevice) return;
        _managingDevice = true;
        ManagedDeviceItems.IsEnabled = DevicesActionButton.IsEnabled = false;
        DevicesActionButton.Content = adding ? "识别中…" : "正在移除…";
        DevicesError.Visibility = Visibility.Collapsed;
        try
        {
            if (adding) await _controller.AddPanelDeviceAsync(device);
            else await _controller.RemovePanelDeviceAsync(device.Address);
            _addingDevices = false;
            FillDeviceManager();
            Render(_controller.State);
            if (!adding && PanelDeviceStore.Read().Length == 0)
            {
                CloseRedmiSettings(animated: false);
                _settingsMotion.Open = false;
                SettleOverlay(SettingsPage, SettingsContent, _settingsMotion);
                AnimateOverlay(DevicesPage, DevicesContent, _devicesMotion, false, false);
            }
        }
        catch (Exception ex) { DevicesError.Text = ex.Message; DevicesError.Visibility = Visibility.Visible; FillDeviceManager(); }
        finally { _managingDevice = false; ManagedDeviceItems.IsEnabled = DevicesActionButton.IsEnabled = true; }
    }

    private void DevicesAction_Click(object sender, RoutedEventArgs e)
    {
        if (_addingDevices) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        else { _addingDevices = true; FillDeviceManager(); }
    }
    private void DevicesBack_Click(object sender, RoutedEventArgs e)
    {
        if (_addingDevices && PanelDeviceStore.Read().Length > 0) { _addingDevices = false; FillDeviceManager(); return; }
        AnimateOverlay(DevicesPage, DevicesContent, _devicesMotion, false, false);
        if (!_managerFromSettings && _settingsMotion.Open) AnimateOverlay(SettingsPage, SettingsContent, _settingsMotion, false, false, false);
    }
}
