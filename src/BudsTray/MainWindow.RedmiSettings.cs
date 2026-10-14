using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;

namespace RedmiBudsTray;

public partial class MainWindow
{
    private readonly OverlayMotion _redmiMotion = new();
    private bool _redmiSettingsUpdating, _renderingRedmiSettings;

    private void RenderRedmiSettings(BudsState state)
    {
        var available = state.Connected && !state.IsHeadphone && (state.Protocol ?? DeviceProtocols.Identify(state.DeviceName)?.Protocol) == EarphoneProtocol.Xiaomi;
        var hasInformation = state.Firmware is not null || state.DualDevice.HasValue || state.EqPreset.HasValue ||
            state.AdaptiveAnc.HasValue || state.AdaptiveSound.HasValue || state.AutoAnswer.HasValue || state.AncStrength.HasValue;
        RedmiSettingsEntry.Visibility = available && hasInformation ? Visibility.Visible : Visibility.Collapsed;
        if (!available && _redmiMotion.Open) CloseRedmiSettings(animated: false);
        _renderingRedmiSettings = true;
        try
        {
            RedmiFirmwareRow.Visibility = state.Firmware is not null ? Visibility.Visible : Visibility.Collapsed;
            RedmiFirmwareText.Text = state.Firmware ?? "—";
            RedmiDualRow.Visibility = state.DualDevice.HasValue ? Visibility.Visible : Visibility.Collapsed;
            RedmiDualText.Text = state.DualDevice == true ? "已开启" : "已关闭";
            RedmiEqRow.Visibility = state.EqPreset.HasValue ? Visibility.Visible : Visibility.Collapsed;
            RedmiEqText.Text = state.EqPreset switch { 0 => "标准", 1 => "人声", 5 => "低音增强", 6 => "高音增强", 10 => "自定义", _ => "—" };
            RedmiStrengthRow.Visibility = state.AncStrength.HasValue && state.NoiseMode == NoiseMode.Anc ? Visibility.Visible : Visibility.Collapsed;
            RedmiStrengthText.Text = state.AncStrength.HasValue ? $"{state.AncStrength + 1} / 20" : "—";
            void SetToggle(FrameworkElement row, CheckBox toggle, bool? value)
            {
                row.Visibility = value.HasValue ? Visibility.Visible : Visibility.Collapsed;
                toggle.IsChecked = value;
                toggle.IsEnabled = available && state.CanControl && value.HasValue && !_redmiSettingsUpdating;
            }
            SetToggle(RedmiAdaptiveAncRow, RedmiAdaptiveAncToggle, state.AdaptiveAnc);
            SetToggle(RedmiAdaptiveSoundRow, RedmiAdaptiveSoundToggle, state.AdaptiveSound);
            SetToggle(RedmiAutoAnswerRow, RedmiAutoAnswerToggle, state.AutoAnswer);
        }
        finally { _renderingRedmiSettings = false; }
    }

    private void RedmiSettings_Click(object sender, RoutedEventArgs e)
    {
        RedmiSettingsHint.Text = "";
        AnimateOverlay(RedmiDevicePage, RedmiDeviceContent, _redmiMotion, true, false);
    }

    private void CloseRedmiSettings(bool animated = true) => AnimateOverlay(RedmiDevicePage, RedmiDeviceContent, _redmiMotion, false, false, animated);
    private void RedmiSettingsBack_Click(object sender, RoutedEventArgs e) => CloseRedmiSettings();

    private async void RedmiSetting_Click(object sender, RoutedEventArgs e)
    {
        if (_renderingRedmiSettings || _redmiSettingsUpdating || sender is not CheckBox { Tag: string tag } toggle ||
            !Enum.TryParse<RedmiSetting>(tag, out var setting)) return;
        var target = toggle.IsChecked == true;
        _redmiSettingsUpdating = true;
        RedmiSettingsHint.Text = "";
        RenderRedmiSettings(_controller.State);
        try { await _controller.SetRedmiSettingAsync(setting, target); }
        catch (Exception ex) { RedmiSettingsHint.Text = ex.Message; }
        finally { _redmiSettingsUpdating = false; RenderRedmiSettings(_controller.State); }
    }
}
