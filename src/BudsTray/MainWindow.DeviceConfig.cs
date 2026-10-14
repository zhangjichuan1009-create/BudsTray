using System.Windows;
using Button = System.Windows.Controls.Button;
namespace RedmiBudsTray;

public partial class MainWindow
{
    private readonly OverlayMotion _configMotion = new();
    private ulong? _configAddress;
    private EarphoneProtocol? _configProtocol;
    private bool _savingConfig, _modeUpdating;
    private void ShowDeviceConfig(PairedBluetoothDevice device)
    {
        var stored = PanelDeviceStore.Read().FirstOrDefault(d => d.Address == device.Address);
        if (stored is null) return;
        _configAddress = device.Address;
        ConfigDeviceName.Text = device.Name;
        _configProtocol = stored.ManualProtocol;
        ConfigAncToggle.IsChecked = stored.AncOverride ?? stored.Profile?.Anc ?? true;
        ConfigTransparencyToggle.IsChecked = stored.TransparencyOverride ?? stored.Profile?.Transparency ?? true;
        RenderConfigProtocol();
        AnimateOverlay(DeviceConfigPage, DeviceConfigContent, _configMotion, true, false);
    }
    private void RenderConfigProtocol()
    {
        foreach (var button in new[] { ConfigAutoProtocol, ConfigXiaomiProtocol, ConfigOneMoreProtocol })
            button.Background = (System.Windows.Media.Brush)Resources[(button.Tag as string) == (_configProtocol?.ToString() ?? "Auto") ? "AccentSoft" : "CardBackground"];
    }
    private void ConfigProtocol_Click(object sender, RoutedEventArgs e)
    {
        if (_savingConfig || sender is not Button { Tag: string tag }) return;
        _configProtocol = Enum.TryParse<EarphoneProtocol>(tag, out var protocol) ? protocol : null;
        RenderConfigProtocol();
    }
    private async void ConfigSave_Click(object sender, RoutedEventArgs e) => await SaveDeviceConfigAsync(false);
    private async void ConfigReset_Click(object sender, RoutedEventArgs e) => await SaveDeviceConfigAsync(true);
    private async Task SaveDeviceConfigAsync(bool reset)
    {
        if (_savingConfig || _configAddress is not ulong address) return;
        _savingConfig = true; DeviceConfigContent.IsEnabled = false;
        try
        {
            await _controller.ConfigurePanelDeviceAsync(address, reset ? null : _configProtocol,
                reset ? null : ConfigAncToggle.IsChecked == true, reset ? null : ConfigTransparencyToggle.IsChecked == true);
            AnimateOverlay(DeviceConfigPage, DeviceConfigContent, _configMotion, false, false);
            FillDeviceManager(); Render(_controller.State);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "设置未完成", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { _savingConfig = false; DeviceConfigContent.IsEnabled = true; }
    }
    private void DeviceConfigBack_Click(object sender, RoutedEventArgs e) => AnimateOverlay(DeviceConfigPage, DeviceConfigContent, _configMotion, false, false);
}
