using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace RedmiBudsTray;

public partial class MainWindow : Window
{
    private readonly BudsController _controller = new();
    private readonly Duration _quick = new(TimeSpan.FromMilliseconds(170));
    private Forms.NotifyIcon? _trayIcon;
    private System.Windows.Threading.DispatcherTimer? _refreshTimer;
    private bool _exitRequested;
    private bool _renderingAutoStart;
    private readonly PopupLifecycle _popup = new();
    private bool _hiding => _popup.Phase == PopupPhase.Closing;
    private bool _showing => _popup.Phase == PopupPhase.Opening;
    private bool? _trayConnected;

    public MainWindow()
    {
        InitializeComponent();
        InitializeExperience();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            if (_pickerMotion.Open) CloseDevicePicker(); else HideAnimated();
            e.Handled = true;
        };
        PreviewMouseDown += (_, e) =>
        {
            // Record the gesture's origin before polling sees a pointer outside the card.
            _outsideButtonDown = true;
            _outsidePress = false;
            if (!_pickerMotion.Open) return;
            var source = e.OriginalSource as DependencyObject;
            while (source is not null)
            {
                if (ReferenceEquals(source, DevicePicker) || ReferenceEquals(source, DevicePickerButton)) return;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            CloseDevicePicker();
        };
        SourceInitialized += (_, _) =>
        {
            ApplyWindows11Frame(); ApplyTheme();
        };
        Closing += OnClosing;
        _controller.StateChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (!_exitRequested) Render(_controller.State); });
        Loaded += (_, _) => AnimateModeIndicator(_presentedNoiseMode, false);
        ModeSelectorGrid.SizeChanged += (_, _) => AnimateModeIndicator(_presentedNoiseMode, false);
        try { _renderingAutoStart = true; AutoStartToggle.IsChecked = AutoStartService.IsEnabled; }
        finally { _renderingAutoStart = false; }
    }

    public void InitializeTray()
    {
        _trayIcon = new Forms.NotifyIcon { Icon = CreateTrayIcon(false, _darkTaskbar), Text = _controller.State.DeviceName, Visible = true };
        _trayIcon.MouseDown += (_, e) => { if (e.Button == Forms.MouseButtons.Left) BeginTrayClick(); };
        _trayIcon.MouseClick += (_, e) =>
        {
            var closes = _trayClickCloses;
            Dispatcher.BeginInvoke(new Action(() => { if (_exitRequested) return; if (e.Button == Forms.MouseButtons.Left) CompleteTrayClick(closes); else if (e.Button == Forms.MouseButtons.Right) ShowTrayMenu(); }));
        };
        _ = _controller.StartAsync();
        _refreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += async (_, _) => {
            if (_slideFrame is not null) return;
            await _controller.RefreshAsync();
            await UpdateVolumeAsync();
        };
        _refreshTimer.Start();
        Render(_controller.State);
        if (Environment.GetCommandLineArgs().Any(x => x.Equals("--demo", StringComparison.OrdinalIgnoreCase))) Dispatcher.BeginInvoke(ShowPanel);
        else if (PanelDeviceStore.Read().Length == 0 && !Environment.GetCommandLineArgs().Contains("--autostart")) Dispatcher.BeginInvoke(ShowPanel);
    }

    private TrayMenuWindow? _trayMenu;
    private void ShowTrayMenu()
    {
        if (_exitRequested) return;
        SettleHidden(PopupPhase.Menu);
        _trayMenu?.Dismiss();
        var menuRevision = _popup.Revision;
        void MenuAction(Action action) { if (!_exitRequested && _popup.Revision == menuRevision) action(); }
        var menu = new TrayMenuWindow(() => MenuAction(ShowPanel), () => MenuAction(() => { ShowPanel(); ShowSettings(); }), () => MenuAction(ExitApplication));
        _trayMenu = menu;
        foreach (var key in new[] { "PanelBackground", "TextPrimary", "TextSecondary", "SubtleBorder", "HoverBackground" })
            menu.Resources[key] = Resources[key];
        menu.Closed += (_, _) => { if (ReferenceEquals(_trayMenu, menu)) _trayMenu = null; _popup.Complete(menuRevision, PopupPhase.Menu, PopupPhase.Hidden); };
        var point = Forms.Cursor.Position;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, 2), ref info)) { menu.Dismiss(); return; }
        var monitor = MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, 2);
        var scale = (GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi : GetDpiForSystem()) / 96.0;
        var width = (int)Math.Round(menu.Width * scale); var height = (int)Math.Round(menu.Height * scale);
        var x = Math.Clamp(point.X - width, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        var y = Math.Clamp(point.Y - height - (int)(6 * scale), info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        menu.Left = x / scale; menu.Top = y / scale;
        menu.Show();
        var hwnd = new WindowInteropHelper(menu).Handle;
        var corner = 2; DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        var dark = _darkApp ? 1 : 0; DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        menu.Activate();
    }

    private static Drawing.Icon CreateTrayIcon(bool connected = false, bool darkTaskbar = false, uint dpi = 0)
    {
        // Render directly at the shell's DPI-scaled icon size: no low-resolution resize.
        var size = Math.Max(16, (int)Math.Round(16 * (dpi == 0 ? GetDpiForSystem() : dpi) / 96.0));
        using var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Drawing.Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.Clear(Drawing.Color.Transparent);
        // Grayscale alpha antialiasing avoids ClearType's light/color fringe on transparency.
        g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Drawing.Font("Segoe Fluent Icons", size * 0.90f, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Pixel);
        var color = darkTaskbar ? Drawing.Color.White : Drawing.Color.Black;
        using var ink = new Drawing.SolidBrush(color);
        using var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
        g.DrawString("\uE7F6", font, ink, new Drawing.RectangleF(0, 0, size, size), format);
        if (!connected)
        {
            using var slash = new Drawing.Pen(color, Math.Max(1.5f, size * 0.085f));
            g.DrawLine(slash, size * 0.18f, size * 0.82f, size * 0.82f, size * 0.18f);
        }
        // Keep edge RGB pure black; coverage is represented only by alpha.
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                bitmap.SetPixel(x, y, Drawing.Color.FromArgb(bitmap.GetPixel(x, y).A, color.R, color.G, color.B));
        var handle = bitmap.GetHicon(); using var temporary = Drawing.Icon.FromHandle(handle); var icon = (Drawing.Icon)temporary.Clone(); DestroyIcon(handle); return icon;
    }

    private void TogglePanel() { if (_popup.Phase is PopupPhase.Open or PopupPhase.Opening) HideAnimated(); else ShowPanel(); }

    private bool _trayClickCloses;
    private void BeginTrayClick() => _trayClickCloses = _popup.Phase is PopupPhase.Open or PopupPhase.Opening;
    private void CompleteTrayClick(bool closes)
    {
        // Focus loss / a close completion between mouse down and up must not reopen it.
        if (closes) HideAnimated(); else ShowPanel();
    }

    private void ShowPanel()
    {
        if (_exitRequested || _showing) return;
        if (_popup.Phase == PopupPhase.Open) { Activate(); return; }
        CancelPanelResize(settle: true);
        _trayMenu?.Dismiss();
        StopSlide();
        Render(_controller.State);
        SoundPanel.BeginAnimation(OpacityProperty, null);
        SoundPanel.Opacity = 1;
        var generation = _popup.Begin(PopupPhase.Opening);
        _anchor = Forms.Cursor.Position;
        var destination = PlaceOnMonitor(_anchor);
        var start = destination;
        var offset = (int)Math.Round(28 * _trayDpi / 96.0);
        start.Top += offset; start.Bottom += offset;
        Top = start.Top / (_trayDpi / 96.0);
        Show(); Activate();
        UpdateLayout();
        AnimateModeIndicator(_presentedNoiseMode, false);
        WatchOutsideClicks();
        _controller.PanelVisible = true;
        if (_refreshTimer != null) _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _transitionDeadline = Environment.TickCount64 + 350;
        _openDestination = destination;
        SlideNative(start, destination, 200, () => FinishOpening(generation));
    }

    private void HideAnimated()
    {
        if (_popup.Phase is not (PopupPhase.Opening or PopupPhase.Open) || !IsVisible) return;
        CancelPanelResize(settle: false);
        var generation = _popup.Begin(PopupPhase.Closing);
        GetWindowRect(new WindowInteropHelper(this).Handle, out var start);
        var end = start;
        var offset = (int)Math.Round(28 * GetDpiForWindow(new WindowInteropHelper(this).Handle) / 96.0);
        end.Top += offset; end.Bottom += offset;
        _transitionDeadline = Environment.TickCount64 + 300;
        SlideNative(start, end, 140, () => FinishHiding(generation));
    }

    public void ShowForPreview() => ShowPanel();

    public void SavePreview(string path)
    {
        if (Environment.GetCommandLineArgs().Contains("--menu-preview"))
        {
            ShowTrayMenu();
            var menu = _trayMenu!;
            menu.UpdateLayout();
            var menuDpi = VisualTreeHelper.GetDpi(menu);
            var menuBitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth * menuDpi.DpiScaleX), (int)Math.Ceiling(menu.ActualHeight * menuDpi.DpiScaleY), menuDpi.PixelsPerInchX, menuDpi.PixelsPerInchY, PixelFormats.Pbgra32);
            menuBitmap.Render(menu);
            var menuEncoder = new PngBitmapEncoder(); menuEncoder.Frames.Add(BitmapFrame.Create(menuBitmap));
            using var menuStream = File.Create(path); menuEncoder.Save(menuStream);
            menu.Dismiss(); return;
        }
        if (Environment.GetCommandLineArgs().Contains("--busy-preview"))
        {
            _userConnecting = true;
            Render(new BudsState("Redmi Buds 8 Pro", "未连接", false, false));
        }
        if (Environment.GetCommandLineArgs().Contains("--offline-preview"))
            Render(new BudsState("Redmi Buds 8 Pro", "未连接", false, false, Hint: "耳机暂时不在附近"));
        if (Environment.GetCommandLineArgs().Contains("--settings-preview"))
            SettingsPage.Visibility = Visibility.Visible;
        if (Environment.GetCommandLineArgs().Contains("--redmi-settings-preview"))
        {
            Render(new("Redmi Buds 8 Pro", "演示模式", true, true, 83, 79, 65, NoiseMode: NoiseMode.Anc,
                Firmware: "3.2.2.6", DualDevice: true, AdaptiveAnc: true, AdaptiveSound: false, AutoAnswer: false, EqPreset: 0, AncStrength: 19));
            _settingsMotion.Open = _redmiMotion.Open = true;
            SettleOverlay(SettingsPage, SettingsContent, _settingsMotion);
            SettleOverlay(RedmiDevicePage, RedmiDeviceContent, _redmiMotion);
        }
        if (Environment.GetCommandLineArgs().Contains("--wear-preview"))
            Render(_controller.State with { LeftWorn = true, RightWorn = false });
        if (Environment.GetCommandLineArgs().Contains("--sono-preview"))
            Render(new("1MORE SonoFlow S", "演示模式", true, true, NoiseMode: NoiseMode.Anc, IsHeadphone: true, HeadphoneBattery: 60, Firmware: "1.0.1", DualDevice: false, Ldac: true));
        if (Environment.GetCommandLineArgs().Contains("--devices-preview"))
        {
            FillDevicePicker(new[] { (new PairedBluetoothDevice("REDMI Buds 8 Pro", 0x100000000001), true), (new PairedBluetoothDevice("1MORE SonoFlow S", 0x100000000002), true) });
            DevicePicker.Visibility = Visibility.Visible;
        }
        if (Environment.GetCommandLineArgs().Contains("--charging-preview"))
        {
            LeftBatteryText.BeginAnimation(OpacityProperty, null); LeftBatteryText.Text = "96%";
            RightBatteryText.BeginAnimation(OpacityProperty, null); RightBatteryText.Text = "93%";
            CaseBatteryText.BeginAnimation(OpacityProperty, null); CaseBatteryText.Text = "100%";
            CaseChargingText.Visibility = Visibility.Visible;
        }
        if (Environment.GetCommandLineArgs().Contains("--demo"))
        {
            if (Environment.GetCommandLineArgs().Contains("--device-config-preview"))
            {
                ShowDeviceManager(false);
                SettleOverlay(DevicesPage, DevicesContent, _devicesMotion);
                var device = PanelDeviceStore.Read().First();
                ShowDeviceConfig(new(device.Name, device.Address));
                SettleOverlay(DeviceConfigPage, DeviceConfigContent, _configMotion);
            }
            if (Environment.GetCommandLineArgs().Contains("--empty-devices-preview"))
            {
                foreach (var device in PanelDeviceStore.Read()) _controller.RemovePanelDeviceAsync(device.Address).GetAwaiter().GetResult();
                Render(_controller.State);
                CancelPanelResize(settle: true);
                PlaceOnMonitor(_anchor);
            }
            if (Environment.GetCommandLineArgs().Contains("--manager-preview"))
            {
                foreach (var device in PanelDeviceStore.Read()) if (device.Profile is not null) PanelDeviceStore.UpdateProfile(device.Address, device.Profile);
                ShowDeviceManager(false);
                SettleOverlay(DevicesPage, DevicesContent, _devicesMotion);
            }
            if (Environment.GetCommandLineArgs().Contains("--add-devices-preview"))
            {
                ShowDeviceManager(true);
                FillDeviceManagerRows([new("REDMI Buds 8 Pro", 1), new("1MORE SonoFlow S", 2), new("我的蓝牙耳机", 3)], [], true);
                SettleOverlay(DevicesPage, DevicesContent, _devicesMotion);
            }
        }
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private void Render(BudsState state)
    {
        if (_slideFrame is not null) { _pendingState = state; return; }
        var bluetoothConnected = state.BluetoothConnected || state.Connected;
        DeviceNameText.Text = state.DeviceName;
        Title = state.DeviceName;
        var profile = DeviceProtocols.Identify(state.DeviceName);
        ProtocolInfoCard.Visibility = Visibility.Visible;
        var managedCount = PanelDeviceStore.Read().Length;
        UpdateDeviceAvailability(managedCount);
        RenderDeviceGuide(managedCount);
        if (_devicesMotion.Open && !_managingDevice && _managerStamp != System.Text.Json.JsonSerializer.Serialize(PanelDeviceStore.Read())) FillDeviceManager();
        StatusText.Text = bluetoothConnected ? (state.Status == "演示模式" ? "演示模式" : "已连接") : "未连接";
        StatusDot.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(bluetoothConnected ? "#18A558" : "#8A8886"));
        AnimateBattery(LeftBatteryText, FormatBattery(state.LeftBattery, state.LeftCharging));
        AnimateBattery(RightBatteryText, FormatBattery(state.RightBattery, state.RightCharging));
        AnimateBattery(CaseBatteryText, FormatBattery(state.CaseBattery, state.CaseCharging));

        AncButton.IsChecked = state.NoiseMode == NoiseMode.Anc;
        TransparencyButton.IsChecked = state.NoiseMode == NoiseMode.Transparency;
        OffButton.IsChecked = state.NoiseMode == NoiseMode.Off;
        AncButton.Visibility = (state.AncSupported ?? state.CanControl) ? Visibility.Visible : Visibility.Collapsed;
        TransparencyButton.Visibility = (state.TransparencySupported ?? state.CanControl) ? Visibility.Visible : Visibility.Collapsed;
        AncButton.IsEnabled = TransparencyButton.IsEnabled = OffButton.IsEnabled = state.CanControl && !_modeUpdating;
        _presentedNoiseMode = state.NoiseMode;
        AnimateModeIndicator(state.NoiseMode, true);

        ShowEarStatus(LeftChargingText, state.LeftCharging, state.LeftWorn);
        ShowEarStatus(RightChargingText, state.RightCharging, state.RightWorn);
        CaseChargingText.Visibility = state.CaseCharging ? Visibility.Visible : Visibility.Hidden;
        var showConnected = state.Connected && BatteryPanel.Visibility != Visibility.Visible && HeadphonePanel.Visibility != Visibility.Visible;
        BatteryPanel.Visibility = SoundPanel.Visibility = state.Connected ? Visibility.Visible : Visibility.Collapsed;
        if (!state.CanControl || AncButton.Visibility != Visibility.Visible && TransparencyButton.Visibility != Visibility.Visible) SoundPanel.Visibility = Visibility.Collapsed;
        if (!state.CanControl && state.LeftBattery is null && state.RightBattery is null && state.CaseBattery is null)
            BatteryPanel.Visibility = Visibility.Collapsed;
        if (state.IsHeadphone) BatteryPanel.Visibility = Visibility.Collapsed;
        HeadphonePanel.Visibility = state.Connected && state.IsHeadphone ? Visibility.Visible : Visibility.Collapsed;
        HeadphoneBatteryText.Text = state.HeadphoneBattery is int percent ? $"{percent}%" : "—";
        HeadphoneChargeArc.Data = BatteryArc(state.HeadphoneBattery);
        var roomyBuds = state.Connected && !state.IsHeadphone;
        SetPanelLayout(roomyBuds);
        RenderVolume(state);
        OneMoreSettings.Visibility = state.Connected && (state.Protocol ?? profile?.Protocol) == EarphoneProtocol.OneMore &&
            (state.DualDevice.HasValue || state.Ldac.HasValue || state.Firmware is not null) ? Visibility.Visible : Visibility.Collapsed;
        OneMoreDualRow.Visibility = state.DualDevice.HasValue ? Visibility.Visible : Visibility.Collapsed;
        OneMoreLdacRow.Visibility = state.Ldac.HasValue ? Visibility.Visible : Visibility.Collapsed;
        OneMoreFirmwareRow.Visibility = state.Firmware is not null ? Visibility.Visible : Visibility.Collapsed;
        OneMoreLdacRow.Margin = new Thickness(0, state.DualDevice.HasValue ? 16 : 0, 0, 0);
        OneMoreFirmwareRow.Margin = new Thickness(0, state.DualDevice.HasValue || state.Ldac.HasValue ? 16 : 0, 0, 0);
        DualDeviceStatus.Text = state.DualDevice is bool dual ? (dual ? "已开启" : "已关闭") : "—";
        LdacStatus.Text = state.Ldac is bool ldac ? (ldac ? "已开启" : "已关闭") : "—";
        FirmwareText.Text = state.Firmware ?? "—";
        RenderRedmiSettings(state);
        if (showConnected && IsVisible && _popup.Phase == PopupPhase.Open)
        {
            BatteryPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        }
        DisconnectedPanel.Visibility = state.Connected ? Visibility.Collapsed : Visibility.Visible;
        ConnectButton.Visibility = state.Connected ? Visibility.Collapsed : Visibility.Visible;
        DisconnectButton.Visibility = state.Connected || state.BluetoothConnected ? Visibility.Visible : Visibility.Collapsed;
        UpdateConnectionFeedback(state);
        CheckLowBattery(state);
        if (_trayIcon != null)
        {
            if (_trayConnected != bluetoothConnected)
            {
                var old = _trayIcon.Icon;
                _trayIcon.Icon = CreateTrayIcon(bluetoothConnected, _darkTaskbar, _trayDpi);
                old?.Dispose();
                _trayConnected = bluetoothConnected;
            }
            var summary = !bluetoothConnected ? $"{state.DeviceName} · 未连接" : state.IsHeadphone && state.HeadphoneBattery is int headphonePercent ? $"{state.DeviceName} · {headphonePercent}%" : state.Connected && state.LeftBattery is int l && state.RightBattery is int r ? $"{state.DeviceName} · 左 {l}% · 右 {r}%" : $"{state.DeviceName} · 已连接";
            _trayIcon.Text = summary.Length <= 63 ? summary : summary[..63];
        }
    }

    private void AnimateBattery(System.Windows.Controls.TextBlock target, string text)
    {
        if (target.Text == text) return;
        var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(70)));
        fadeOut.Completed += (_, _) =>
        {
            target.Text = text;
            target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(160))));
        };
        target.BeginAnimation(OpacityProperty, fadeOut);
    }

    private double? _modeIndicatorTarget;
    private NoiseMode? _presentedNoiseMode;
    private void AnimateModeIndicator(NoiseMode? mode, bool animated)
    {
        if (mode is null)
        {
            ModeIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _modeIndicatorTarget = null;
            ModeIndicator.Opacity = 0;
            return;
        }
        if (ModeSelectorGrid.ActualWidth <= 0) return;
        var buttons = new[] { AncButton, TransparencyButton, OffButton }.Where(b => b.Visibility == Visibility.Visible).ToArray();
        var index = Array.FindIndex(buttons, b => b.Tag is string tag && int.TryParse(tag, out var number) && number == (int)mode);
        if (index < 0 || buttons.Length == 0) { ModeIndicator.Opacity = 0; return; }
        var width = ModeSelectorGrid.ActualWidth / buttons.Length;
        ModeIndicator.Width = width;
        var target = index * width;
        var previousTarget = _modeIndicatorTarget;
        _modeIndicatorTarget = target;
        var firstPosition = ModeIndicator.Opacity == 0 || previousTarget is null;
        ModeIndicator.Opacity = 1;
        if (!animated || firstPosition || !IsVisible || _popup.Phase != PopupPhase.Open || !SystemParameters.ClientAreaAnimation)
        {
            ModeIndicatorTransform.BeginAnimation(TranslateTransform.XProperty, null);
            ModeIndicatorTransform.X = target;
            return;
        }
        if (Math.Abs(previousTarget!.Value - target) < 0.25) return;
        ModeIndicatorTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(ModeIndicatorTransform.X, target, new Duration(TimeSpan.FromMilliseconds(220)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private static string FormatBattery(int? value, bool charging) => value is null ? "—" : $"{value}%";

    private static Geometry BatteryArc(int? percent)
    {
        if (percent is null or <= 0) return Geometry.Empty;
        // Match the ring's stroke centre: 70px diameter, 3px inset, 4px stroke.
        const double centre = 35, radius = 30;
        if (percent >= 100) return new EllipseGeometry(new System.Windows.Point(centre, centre), radius, radius);
        var angle = percent.Value / 100.0 * Math.PI * 2;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new System.Windows.Point(centre, centre - radius), false, false);
            context.ArcTo(new System.Windows.Point(centre + radius * Math.Sin(angle), centre - radius * Math.Cos(angle)),
                new System.Windows.Size(radius, radius), 0, percent > 50, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }


    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (PanelDeviceStore.Read().Length == 0)
        {
            ShowDeviceManager(true);
            return;
        }
        _userConnecting = true;
        UpdateConnectionFeedback(_controller.State);
        try { await _controller.ReconnectAsync(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "连接未完成", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { _userConnecting = false; UpdateConnectionFeedback(_controller.State); }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _userDisconnecting = true;
        UpdateConnectionFeedback(_controller.State);
        try { await _controller.DisconnectAsync(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "断开未完成", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { _userDisconnecting = false; UpdateConnectionFeedback(_controller.State); }
    }

    private async void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (_modeUpdating || sender is not System.Windows.Controls.RadioButton { Tag: string tag } || !int.TryParse(tag, out var value)) return;
        _modeUpdating = true; Render(_controller.State);
        try { await _controller.SetNoiseModeAsync((NoiseMode)value); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "模式未确认", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { _modeUpdating = false; Render(_controller.State); }
    }

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        if (_renderingAutoStart) return;
        try { AutoStartService.SetEnabled(AutoStartToggle.IsChecked == true); SettingsHint.Text = AutoStartToggle.IsChecked == true ? "已开启开机自启动，请保留当前应用文件夹。" : "已关闭开机自启动"; }
        catch (Exception ex) { _renderingAutoStart = true; AutoStartToggle.IsChecked = !AutoStartToggle.IsChecked; _renderingAutoStart = false; SettingsHint.Text = ex.Message; }
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => HideAnimated();
    private void ShowEarStatus(System.Windows.Controls.TextBlock label, bool charging, bool? worn)
    {
        label.Text = charging ? "充电中" : worn == true ? "已佩戴" : "未佩戴";
        label.Visibility = charging || worn.HasValue ? Visibility.Visible : Visibility.Hidden;
        label.Foreground = (System.Windows.Media.Brush)Resources[charging ? "ChargingForeground" : "TextSecondary"];
        label.FontSize = 11;
    }
    // The dismissal watcher owns focus-loss handling, after pointer gestures complete.
    // Immediate hiding here races the shell's tray click and interrupts slider capture.
    private void Window_Deactivated(object sender, EventArgs e) { }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void Back_Click(object sender, RoutedEventArgs e) => AnimateOverlay(SettingsPage, SettingsContent, _settingsMotion, false, false);

    private void ExitApplication()
    {
        _exitRequested = true; _refreshTimer?.Stop(); StopSlide(); _controller.Dispose();
        _volumeWriteTimer?.Stop(); _pendingVolume = null;
        SettleHidden(PopupPhase.Exited);
        _trayMenu?.Dismiss();
        if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
        Close(); System.Windows.Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, CancelEventArgs e) { if (!_exitRequested) { e.Cancel = true; SettleHidden(PopupPhase.Hidden); } }

    private void ApplyWindows11Frame()
    {
        var hwnd = new WindowInteropHelper(this).Handle; var corner = 2; DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        // Keep a non-layered HWND and full window opacity; DWM owns the backdrop.
    }


    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}
