namespace RedmiBudsTray;

public enum NoiseMode { Off = 0, Anc = 1, Transparency = 2 }

public sealed record BudsState(
    string DeviceName, string Status, bool Connected, bool CanControl,
    int? LeftBattery = null, int? RightBattery = null, int? CaseBattery = null,
    bool LeftCharging = false, bool RightCharging = false, bool CaseCharging = false,
    NoiseMode? NoiseMode = null, string Hint = "", bool BluetoothConnected = false,
    bool? LeftWorn = null, bool? RightWorn = null,
    bool IsHeadphone = false, int? HeadphoneBattery = null, string? Firmware = null, bool? DualDevice = null, bool? Ldac = null,
    bool? AdaptiveAnc = null, bool? AdaptiveSound = null, bool? AutoAnswer = null, byte? EqPreset = null, byte? AncStrength = null,
    EarphoneProtocol? Protocol = null, bool? AncSupported = null, bool? TransparencySupported = null);

public sealed partial class BudsController : IDisposable
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SemaphoreSlim _manualGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private RfcommTransport? _transport;
    private bool _isOneMore;
    private EarphoneProfile? _profile;
    private ulong? _deviceAddress = ConnectionPreferences.SelectedAddress;
    public ulong? DeviceAddress => _deviceAddress;
    private OneMoreProtocol _oneMore = new();
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;
    private byte _sequence = (byte)Random.Shared.Next(0, 256);
    private byte _ancStrength;
    private long _noiseReadRevision;
    private byte _transparencyStrength;
    private bool _disposed;
    private bool _connecting;
    private bool _manualDisconnect;
    private bool _manualOperation;
    private CancellationTokenSource? _connectCancellation;
    private long _lastReplyTicks;
    private long _pendingQueryTicks;
    private long _lastQueryTicks;
    private long _lastMetadataQueryTicks;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<RedmiSetting, long> _settingReadRevisions = new();
    public bool PanelVisible { get; set; }
    private int _userIntent;
    private DateTime _retryAfter;
    private int _failedAttempts;
    private bool Paused => _manualDisconnect || !IsDemo && ConnectionPreferences.IsPaused(_deviceAddress);

    public BudsController()
    {
        var device = DeviceProtocols.Choose(PanelDeviceStore.Devices(), _deviceAddress, BluetoothAudioConnection.IsAudioReady);
        _deviceAddress = device?.Address;
        if (device is not null) SetDevice(device);
    }

    private void SetDevice(PairedBluetoothDevice device)
    {
        _profile = PanelDeviceStore.Profile(device);
        var forced = PanelDeviceStore.Read().FirstOrDefault(d => d.Address == device.Address)?.ManualProtocol;
        if (forced.HasValue && _profile?.Protocol != forced) _profile = new(forced.Value, false, forced == EarphoneProtocol.OneMore);
        _isOneMore = _profile?.Protocol == EarphoneProtocol.OneMore;
        _state = new(device.Name, "未连接", false, false, IsHeadphone: _profile?.IsHeadphone == true,
            Protocol: _profile?.Protocol, AncSupported: _profile?.Anc, TransparencySupported: _profile?.Transparency);
    }

    private void CancelConnectionAttempt()
    {
        try { _connectCancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* Attempt finished concurrently. */ }
    }

    public Task SelectDeviceAsync(ulong address) => ReconnectAsync(address, switchOutput: true);

    public async Task ReconnectAsync(ulong? selectedAddress = null, bool switchOutput = false)
    {
        if (_disposed) return;
        var selected = selectedAddress.HasValue ? PanelDeviceStore.Devices().FirstOrDefault(d => d.Address == selectedAddress.Value)
            ?? throw new InvalidOperationException("未找到该耳机，请先在 Windows 中配对") : null;
        if (selected is not null) CancelDeviceProbe(selected.Address);
        var intent = Interlocked.Increment(ref _userIntent);
        CancelConnectionAttempt();
        await _manualGate.WaitAsync();
        _manualOperation = true;
        try
        {
            if (intent != Volatile.Read(ref _userIntent)) return;
            _manualDisconnect = true;
            CancelConnectionAttempt();
            await _connectionGate.WaitAsync();
            try
            {
                await _commandGate.WaitAsync();
                try { CloseTransport(); }
                finally { _commandGate.Release(); }
            }
            finally { _connectionGate.Release(); }
            if (intent != Volatile.Read(ref _userIntent)) return;
            if (selected is not null)
            {
                _deviceAddress = selected.Address;
                if (!IsDemo) ConnectionPreferences.SelectedAddress = selected.Address;
                SetDevice(selected);
                Publish(_state with { Status = "正在连接…" });
            }
            if (!IsDemo) ConnectionPreferences.SetPaused(_deviceAddress, false);
            _manualDisconnect = false;
            _retryAfter = DateTime.MinValue;
            _failedAttempts = 0;
            await ConnectAsync(true);
            if (!_state.Connected) throw new InvalidOperationException(_state.Hint);
            if (!IsDemo && switchOutput && _deviceAddress is ulong address && intent == Volatile.Read(ref _userIntent))
                await Task.Run(() => { if (intent == Volatile.Read(ref _userIntent)) BluetoothAudioConnection.SelectOutput(address); });
        }
        finally { _manualOperation = false; _manualGate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        if (_disposed) return;
        var intent = Interlocked.Increment(ref _userIntent);
        _manualDisconnect = true;
        if (!IsDemo) ConnectionPreferences.SetPaused(_deviceAddress, true);
        AppLog.Write("Manual disconnect: reconnect paused");
        CancelConnectionAttempt();
        CloseTransport();
        await _manualGate.WaitAsync();
        _manualOperation = true;
        try
        {
            if (intent != Volatile.Read(ref _userIntent)) return;
            _manualDisconnect = true;
            if (!IsDemo) ConnectionPreferences.SetPaused(_deviceAddress, true);
            CancelConnectionAttempt();
            await _connectionGate.WaitAsync();
            try
            {
                CloseTransport();
                var device = PanelDeviceStore.Choose(_deviceAddress);
                if (!IsDemo && device is not null)
                {
                    await BluetoothAudioConnection.DisconnectAsync(device.Address, _lifetime.Token);
                }
                Publish(new(_state.DeviceName, "未连接", false, false, IsHeadphone: _profile?.IsHeadphone == true));
            }
            catch (Exception ex)
            {
                Publish(_state with { Connected = false, CanControl = false, Hint = FriendlyError(ex) });
                throw;
            }
            finally { _connectionGate.Release(); }
        }
        finally { _manualOperation = false; _manualGate.Release(); }
    }
    private BudsState _state = new("蓝牙耳机", "未连接", false, false, Hint: "请添加耳机");
    public event EventHandler<BudsState>? StateChanged;
    public BudsState State => _state;
    public async Task ProbeWearingAsync()
    {
        var transport = _transport;
        if (transport?.Connected != true || Paused || _isOneMore) return;
        await _commandGate.WaitAsync();
        try
        {
            await transport.SendAsync(XiaomiProtocol.BuildWearingQuery(NextSequence()));
            await transport.SendAsync(XiaomiProtocol.BuildRuntimeQuery(NextSequence()));
        }
        finally { _commandGate.Release(); }
    }

    public Task StartAsync() => ConnectAsync(startup: true);

    public async Task ConnectAsync(bool manual = false, bool startup = false)
    {
        if (!manual && !startup && DateTime.UtcNow < _retryAfter) return;
        if (PanelDeviceStore.Read().Length == 0) return;
        if (_disposed || Paused || (_manualOperation && !manual) || !await _connectionGate.WaitAsync(0)) return;
        _connecting = true;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _connectCancellation = attempt;
        try
        {
            if (Paused) return;
            AppLog.Write(manual ? "Manual connect started" : "Background connect started");
            if (IsDemo)
            {
                Publish(_state with { Status = "演示模式", Connected = true, CanControl = _profile is not null,
                    LeftBattery = _state.IsHeadphone ? null : 83, RightBattery = _state.IsHeadphone ? null : 79,
                    CaseBattery = _state.IsHeadphone ? null : 65, HeadphoneBattery = _state.IsHeadphone ? 60 : null,
                    NoiseMode = _state.NoiseMode ?? NoiseMode.Anc, Hint = "演示模式" });
                return;
            }
            if (_transport?.Connected == true) { await SendStateQueriesAsync(); return; }

            var device = PanelDeviceStore.Choose(_deviceAddress);
            if (device is null && manual) device = PanelDeviceStore.Choose();
            if (device is null)
            {
                Publish(_state with { Status = "未找到已配对耳机", Connected = false, CanControl = false, Hint = "请先在 Windows 蓝牙设置中配对耳机" });
                return;
            }

            // Background polling never tells the audio driver to reconnect repeatedly.
            // It attaches control only after Windows reports the audio link ready.
            if (!manual && !startup && !await Task.Run(() => BluetoothAudioConnection.IsAudioReady(device.Address))) return;

            Publish(_state with { DeviceName = device.Name, Status = "正在连接…", Connected = false, CanControl = false, Hint = "正在建立持续控制通道" });
            CloseTransport();
            _deviceAddress = device.Address;
            ConnectionPreferences.SelectedAddress = device.Address;
            SetDevice(device);
            _oneMore = new();
            Publish(_state with { Status = "正在连接…" });
            if (manual || startup) await BluetoothAudioConnection.EnsureConnectedAsync(device.Address, attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            if (Paused) return;
            Publish(_state with { BluetoothConnected = true });
            await AttachSelectedProtocolAsync(device, attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            if (Paused) { CloseTransport(); return; }
            attempt.Token.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _lastReplyTicks, DateTime.UtcNow.Ticks);
            _failedAttempts = 0;
            Publish(_state with { DeviceName = device.Name, Status = "已连接", Connected = true, CanControl = _profile is not null, AncSupported = EffectiveAnc, TransparencySupported = EffectiveTransparency, Hint = "控制通道保持连接" });
            StartReader();
            await SendStateQueriesAsync();
        }
        catch (Exception ex) { HandleDisconnect(ex); }
        finally { _connectCancellation = null; _connecting = false; _connectionGate.Release(); }
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (_disposed || _manualOperation || _connecting || !await _refreshGate.WaitAsync(0)) return;
        try
        {
            if (!IsDemo)
            {
                var refreshIntent = Volatile.Read(ref _userIntent);
                var refreshAddress = _deviceAddress;
                var audioState = await Task.Run(() => {
                    var device = PanelDeviceStore.Choose(refreshAddress);
                    return (Device: device, Connected: device is not null && BluetoothAudioConnection.IsAudioReady(device.Address));
                });
                if (_manualOperation || refreshIntent != Volatile.Read(ref _userIntent) || refreshAddress != _deviceAddress) return;
                var connected = audioState.Connected;
                if (audioState.Device is not null && _state.DeviceName != audioState.Device.Name) Publish(_state with { DeviceName = audioState.Device.Name });
                if (connected != _state.BluetoothConnected)
                    Publish(_state with { BluetoothConnected = connected });
                if (!connected && _transport is not null) HandleDisconnect(new InvalidOperationException("蓝牙连接已断开"));
            }
            // Reading Windows state is allowed while paused; no connect/query traffic is sent.
            if (Paused) return;
            var now = DateTime.UtcNow.Ticks;
            var pending = Interlocked.Read(ref _pendingQueryTicks);
            if (_transport?.Connected == true && pending != 0 && now - pending > TimeSpan.FromSeconds(8).Ticks)
            {
                if (PanelDeviceStore.Read().FirstOrDefault(d => d.Address == _deviceAddress)?.ManualProtocol.HasValue == true && _profile?.Verified != true)
                    Interlocked.Exchange(ref _pendingQueryTicks, 0);
                else HandleDisconnect(new TimeoutException("耳机控制通道未响应"));
            }
            if (_transport?.Connected != true) { await ConnectAsync(); return; }
            if (force || StatePollingPolicy.ShouldQuery(now, Interlocked.Read(ref _lastQueryTicks), Interlocked.Read(ref _lastReplyTicks), PanelVisible))
                await SendStateQueriesAsync();
        }
        catch (Exception ex) { HandleDisconnect(ex); }
        finally { _refreshGate.Release(); }
    }

    public async Task SetNoiseModeAsync(NoiseMode mode)
    {
        if (IsDemo)
        {
            if (_profile is not null) _profile = mode switch { NoiseMode.Anc => _profile with { Anc = true }, NoiseMode.Transparency => _profile with { Transparency = true }, _ => _profile };
            PersistObservedProfile();
            Publish(_state with { NoiseMode = mode, AncSupported = EffectiveAnc, TransparencySupported = EffectiveTransparency, Hint = "演示模式 · 切换动画预览" });
            return;
        }
        if (_manualDisconnect || _transport?.Connected != true) { Publish(_state with { Hint = "请先点击连接" }); return; }
        if (_profile is null || !_state.CanControl) throw new InvalidOperationException("请先选择协议并连接控制通道");
        if (mode == NoiseMode.Anc && !EffectiveAnc || mode == NoiseMode.Transparency && !EffectiveTransparency) return;

        var transport = _transport;
        var address = _deviceAddress;
        var intent = Volatile.Read(ref _userIntent);
        bool CurrentSession() => !_disposed && !Paused && ReferenceEquals(transport, _transport) && address == _deviceAddress && intent == Volatile.Read(ref _userIntent);
        long before;
        await _commandGate.WaitAsync();
        try
        {
            if (!CurrentSession()) throw new InvalidOperationException("控制连接已变化");
            before = Interlocked.Read(ref _noiseReadRevision);
            if (_isOneMore)
            {
                await transport.SendAsync(OneMoreProtocol.SetMode(mode));
                await Task.Delay(150, _lifetime.Token);
                if (!CurrentSession()) throw new InvalidOperationException("控制连接已变化");
                await transport.SendAsync(OneMoreProtocol.ModeQuery);
            }
            else
            {
                var strength = mode == NoiseMode.Anc ? _ancStrength : mode == NoiseMode.Transparency ? _transparencyStrength : (byte)0;
                await transport.SendAsync(XiaomiProtocol.BuildSetNoise(mode, NextSequence(), strength));
                await Task.Delay(90, _lifetime.Token);
                if (!CurrentSession()) throw new InvalidOperationException("控制连接已变化");
                await transport.SendAsync(XiaomiProtocol.BuildGetNoise(NextSequence()));
            }
        }
        finally { _commandGate.Release(); }
        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(120, _lifetime.Token);
            if (!CurrentSession()) throw new InvalidOperationException("连接已中断，模式未确认");
            if (Interlocked.Read(ref _noiseReadRevision) > before && _state.NoiseMode == mode) return;
        }
        throw new TimeoutException("耳机未确认此模式，可在设备设置中调整协议或隐藏该功能");
    }

    private async Task AuthenticateAsync()
    {
        if (_transport is null) throw new InvalidOperationException("控制通道未连接");
        var challenge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        await _transport.SendAsync(XiaomiProtocol.BuildAuthChallenge(challenge, NextSequence()));
        var packets = await ReceiveHandshakeAsync(TimeSpan.FromSeconds(3));
        var response = packets.FirstOrDefault(p => p.Opcode == 0x50 && !p.IsRequest);
        if (response is null || !XiaomiProtocol.VerifyAuth(challenge, response))
            throw new InvalidOperationException("小米耳机协议认证未通过");
        await _transport.SendAsync(XiaomiProtocol.BuildAuthStatus(NextSequence()));
        await ReceiveHandshakeAsync(TimeSpan.FromMilliseconds(450));
    }

    private async Task<List<MmaPacket>> ReceiveHandshakeAsync(TimeSpan timeout)
    {
        if (_transport is null) return [];
        var packets = XiaomiProtocol.Decode(await _transport.ReceiveAsync(timeout));
        await RespondToEarbudRequestsAsync(packets);
        return packets;
    }

    private void StartReader()
    {
        _readerCancellation?.Cancel();
        _readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _readerCancellation.Token;
        var sessionTransport = _transport!;
        _readerTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && sessionTransport.Connected)
            {
                try
                {
                    var raw = await sessionTransport.ReceiveAsync(TimeSpan.FromMilliseconds(600));
                    if (token.IsCancellationRequested || !ReferenceEquals(_transport, sessionTransport)) break;
                    if (raw.Length == 0) continue;
                    if (_isOneMore)
                    {
                        ApplyOneMore(_oneMore.Feed(raw));
                        continue;
                    }
                    var packets = XiaomiProtocol.Decode(raw);
                    await RespondToEarbudRequestsAsync(packets);
                    if (token.IsCancellationRequested || !ReferenceEquals(_transport, sessionTransport)) break;
                    ApplyPackets(packets);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) {
                    if (!token.IsCancellationRequested && ReferenceEquals(_transport, sessionTransport)) HandleDisconnect(ex);
                    break;
                }
            }
        }, token);
    }

    private async Task RespondToEarbudRequestsAsync(IEnumerable<MmaPacket> packets)
    {
        if (_manualDisconnect || _disposed) return;
        var transport = _transport;
        if (transport is null) return;
        foreach (var packet in packets.Where(p => p.IsRequest))
        {
            if (packet.Opcode == 0x50 && packet.Data.Length == 17 && packet.Data[0] == 1)
                await transport.SendAsync(XiaomiProtocol.BuildEarbudAuthResponse(packet.Data[1..], packet.Sequence));
            else if (packet.Opcode == 0x51)
                await transport.SendAsync(XiaomiProtocol.BuildAuthConfirmResponse(packet.Sequence));
        }
    }

    private void ApplyPackets(IEnumerable<MmaPacket> packets)
    {
        if (_disposed || _manualDisconnect) return;
        packets = packets.ToArray();
        if (Environment.GetCommandLineArgs().Contains("--wear-probe"))
            foreach (var packet in packets.Where(p => p.Opcode is 0x09 or 0x0E or 0xF4 || p.Opcode == 0xF3 && p.Data.Length >= 3 && p.Data[2] == 0x0C))
                Console.WriteLine($"WEAR FRAME request={packet.IsRequest} opcode={packet.Opcode:X2} status={packet.Status:X2} data={Convert.ToHexString(packet.Data)}");
        var battery = packets.Select(XiaomiProtocol.TryReadBattery).FirstOrDefault(x => x is not null);
        var info = packets.Select(XiaomiProtocol.TryReadDeviceInfo).LastOrDefault(x => x is not null);
        if (info is not null)
        {
            var profile = DeviceProtocols.FromXiaomiInfo(info, _profile);
            if (profile != _profile && _deviceAddress is ulong address)
            {
                _profile = profile;
                PanelDeviceStore.UpdateProfile(address, profile);
            }
        }
        var noise = packets.Where(p => p.Opcode is 0xF3 or 0xF4).Select(XiaomiProtocol.TryReadNoiseDetails).FirstOrDefault(x => x is not null);
        var wearing = packets.Select(XiaomiProtocol.TryReadWearing).LastOrDefault(x => x is not null);
        var settings = packets.Select(XiaomiProtocol.TryReadSettings).Where(x => x is not null).ToArray();
        var strength = packets.Select(XiaomiProtocol.TryReadAncStrength).LastOrDefault(x => x.HasValue);
        if (noise is not null) RememberStrength(noise);
        if (battery is null && noise is null && wearing is null && info is null && settings.Length == 0 && !strength.HasValue) return;
        Interlocked.Exchange(ref _lastReplyTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _pendingQueryTicks, 0);

        var next = _state with { Status = "已连接", Connected = true, CanControl = _profile is not null, Hint = "实时连接 · 刚刚更新" };
        next = next with { Protocol = _profile?.Protocol, AncSupported = _profile?.Anc, TransparencySupported = _profile?.Transparency };
        if (_profile is not null && noise?.Mode == NoiseMode.Anc && _profile.Anc is null) _profile = _profile with { Anc = true };
        if (_profile is not null && noise?.Mode == NoiseMode.Transparency && _profile.Transparency is null) _profile = _profile with { Transparency = true };
        next = next with { AncSupported = EffectiveAnc, TransparencySupported = EffectiveTransparency };
        PersistObservedProfile();
        if (battery is not null)
            next = next with
            {
                LeftBattery = battery.Left.Percent, RightBattery = battery.Right.Percent, CaseBattery = battery.Case.Percent,
                LeftCharging = battery.Left.Charging, RightCharging = battery.Right.Charging, CaseCharging = battery.Case.Charging
            };
        if (noise is not null) next = next with { NoiseMode = noise.Mode };
        if (wearing is not null) next = next with { LeftWorn = wearing.Left, RightWorn = wearing.Right };
        if (info?.Firmware is string firmware) next = next with { Firmware = firmware };
        if (strength.HasValue) next = next with { AncStrength = strength };
        foreach (var reading in settings)
        {
            if (reading!.DualDevice.HasValue) next = next with { DualDevice = reading.DualDevice };
            if (reading.EqPreset.HasValue) next = next with { EqPreset = reading.EqPreset };
            if (reading.AdaptiveAnc.HasValue) next = next with { AdaptiveAnc = reading.AdaptiveAnc };
            if (reading.AdaptiveSound.HasValue) next = next with { AdaptiveSound = reading.AdaptiveSound };
            if (reading.AutoAnswer.HasValue) next = next with { AutoAnswer = reading.AutoAnswer };
        }
        if (next.Firmware != _state.Firmware || next.DualDevice != _state.DualDevice ||
            next.AdaptiveAnc != _state.AdaptiveAnc || next.AdaptiveSound != _state.AdaptiveSound || next.AutoAnswer != _state.AutoAnswer)
            AppLog.Write($"Redmi metadata: firmware={next.Firmware}; dual={next.DualDevice}; adaptiveAnc={next.AdaptiveAnc}; adaptiveSound={next.AdaptiveSound}; autoAnswer={next.AutoAnswer}");
        Publish(next);
        if (noise is not null) Interlocked.Increment(ref _noiseReadRevision);
        // Only actual get/notify replies count as readback, not SET acknowledgements.
        foreach (var reading in settings)
        {
            if (reading!.AdaptiveAnc.HasValue) _settingReadRevisions.AddOrUpdate(RedmiSetting.AdaptiveAnc, 1, (_, revision) => revision + 1);
            if (reading.AdaptiveSound.HasValue) _settingReadRevisions.AddOrUpdate(RedmiSetting.AdaptiveSound, 1, (_, revision) => revision + 1);
            if (reading.AutoAnswer.HasValue) _settingReadRevisions.AddOrUpdate(RedmiSetting.AutoAnswer, 1, (_, revision) => revision + 1);
        }
        if (Environment.GetCommandLineArgs().Contains("--redmi-settings-probe") && (info is not null || settings.Length > 0))
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(next));
    }

    private async Task SendStateQueriesAsync()
    {
        var transport = _transport;
        if (transport?.Connected != true) return;
        await _commandGate.WaitAsync();
        try
        {
            if (_manualDisconnect || _disposed || !ReferenceEquals(transport, _transport)) return;
            var now = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(ref _lastQueryTicks, now);
            Interlocked.CompareExchange(ref _pendingQueryTicks, now, 0);
            if (_isOneMore)
            {
                var queries = new[] { OneMoreProtocol.InfoQuery, OneMoreProtocol.ModeQuery, OneMoreProtocol.DualQuery, OneMoreProtocol.LdacQuery };
                foreach (var query in queries)
                    await transport.SendAsync(query);
                return;
            }
            await transport.SendAsync(XiaomiProtocol.BuildBatteryQuery(NextSequence()));
            await transport.SendAsync(XiaomiProtocol.BuildGetNoise(NextSequence()));
            await transport.SendAsync(XiaomiProtocol.BuildWearingQuery(NextSequence()));
            var metadataInterval = TimeSpan.FromSeconds(PanelVisible ? 15 : 60).Ticks;
            if (now - Interlocked.Read(ref _lastMetadataQueryTicks) >= metadataInterval)
            {
                Interlocked.Exchange(ref _lastMetadataQueryTicks, now);
                await transport.SendAsync(XiaomiProtocol.BuildDeviceInfoQuery(NextSequence()));
                foreach (var setting in new[] { RedmiSetting.DualDevice, RedmiSetting.EqPreset, RedmiSetting.AdaptiveAnc, RedmiSetting.AdaptiveSound, RedmiSetting.AutoAnswer })
                {
                    await Task.Delay(60, _lifetime.Token);
                    if (Paused || !ReferenceEquals(transport, _transport)) return;
                    await transport.SendAsync(XiaomiProtocol.BuildGetSetting(setting, NextSequence()));
                }
            }
        }
        finally { _commandGate.Release(); }
    }

    private void HandleDisconnect(Exception ex)
    {
        AppLog.Write($"Control disconnected: {ex.GetType().Name}: {ex.Message}");
        _retryAfter = DateTime.UtcNow.AddSeconds(Math.Min(30, Math.Pow(2, Math.Min(++_failedAttempts, 5))));
        CloseTransport();
        Publish(_state with { Status = "连接已断开", Connected = false, CanControl = false, Hint = FriendlyError(ex) });
    }

    private void CloseTransport()
    {
        Interlocked.Exchange(ref _pendingQueryTicks, 0);
        Interlocked.Exchange(ref _lastQueryTicks, 0);
        Interlocked.Exchange(ref _lastMetadataQueryTicks, 0);
        _settingReadRevisions.Clear();
        _state = _state with { LeftWorn = null, RightWorn = null, Firmware = null, DualDevice = null, Ldac = null,
            AdaptiveAnc = null, AdaptiveSound = null, AutoAnswer = null, EqPreset = null, AncStrength = null };
        _readerCancellation?.Cancel(); _readerCancellation?.Dispose(); _readerCancellation = null;
        _transport?.Dispose(); _transport = null;
    }

    private void RememberStrength(NoiseReading reading)
    {
        if (reading.Mode == NoiseMode.Anc) _ancStrength = reading.Strength;
        else if (reading.Mode == NoiseMode.Transparency) _transparencyStrength = reading.Strength;
    }

    private void ApplyOneMore(IEnumerable<OneMoreReading> readings)
    {
        if (_disposed || Paused) return;
        foreach (var reading in readings)
        {
            if (_profile is not null) _profile = _profile with { Verified = true };
            Interlocked.Exchange(ref _lastReplyTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _pendingQueryTicks, 0);
            var next = _state;
            if (reading.Command == 0x4E) next = next with { HeadphoneBattery = _profile?.IsHeadphone == true ? reading.Battery : null, Firmware = reading.Firmware };
            if (reading.Mode.HasValue) next = next with { NoiseMode = reading.Mode };
            if (_profile is not null && reading.Mode == NoiseMode.Anc && _profile.Anc is null) _profile = _profile with { Anc = true };
            if (_profile is not null && reading.Mode == NoiseMode.Transparency && _profile.Transparency is null) _profile = _profile with { Transparency = true };
            next = next with { AncSupported = EffectiveAnc, TransparencySupported = EffectiveTransparency };
            PersistObservedProfile();
            if (reading.DualDevice.HasValue) next = next with { DualDevice = reading.DualDevice };
            if (reading.Ldac.HasValue) next = next with { Ldac = reading.Ldac };
            Publish(next);
            if (reading.Mode.HasValue) Interlocked.Increment(ref _noiseReadRevision);
        }
    }

    public async Task SetOneMoreOptionAsync(bool ldac, bool enabled)
    {
        if (!_isOneMore || Paused || _transport?.Connected != true) throw new InvalidOperationException("请先连接万魔协议控制通道");
        if ((ldac ? _state.Ldac : _state.DualDevice) == enabled) return;
        await _commandGate.WaitAsync();
        try
        {
            // Clear the old readback so success cannot be inferred from cached state.
            Publish(ldac ? _state with { Ldac = null } : _state with { DualDevice = null });
            await _transport.SendAsync(ldac ? OneMoreProtocol.SetLdac(enabled) : OneMoreProtocol.SetDual(enabled));
        }
        finally { _commandGate.Release(); }
        // Configuration changes may reboot the SPP endpoint. Reconnect outside the
        // command gate so connection initialization can issue its own queries.
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(500, _lifetime.Token);
            if (Paused) throw new OperationCanceledException("连接已由用户暂停");
            if (_transport?.Connected != true) await ConnectAsync(manual: true);
            if ((ldac ? _state.Ldac : _state.DualDevice) == enabled) return;
            var transport = _transport;
            if (transport?.Connected == true)
            {
                await _commandGate.WaitAsync();
                try { if (ReferenceEquals(transport, _transport)) await transport.SendAsync(ldac ? OneMoreProtocol.LdacQuery : OneMoreProtocol.DualQuery); }
                finally { _commandGate.Release(); }
            }
        }
        throw new TimeoutException("耳机未确认配置，请重新连接后检查");
    }

    private bool IsDemo => Environment.GetCommandLineArgs().Any(x => x.Equals("--demo", StringComparison.OrdinalIgnoreCase));
    private bool? ReadRedmiSetting(RedmiSetting setting) => setting switch
    {
        RedmiSetting.AdaptiveAnc => _state.AdaptiveAnc,
        RedmiSetting.AdaptiveSound => _state.AdaptiveSound,
        RedmiSetting.AutoAnswer => _state.AutoAnswer,
        _ => null
    };

    public async Task SetRedmiSettingAsync(RedmiSetting setting, bool enabled)
    {
        if (setting is not (RedmiSetting.AdaptiveAnc or RedmiSetting.AdaptiveSound or RedmiSetting.AutoAnswer))
            throw new InvalidOperationException("此设置目前仅支持读取");
        if (_isOneMore || !_state.Connected || !_state.CanControl || !ReadRedmiSetting(setting).HasValue)
            throw new InvalidOperationException("尚未读取到该设置，不能修改");
        if (ReadRedmiSetting(setting) == enabled) return;
        if (IsDemo)
        {
            Publish(setting switch
            {
                RedmiSetting.AdaptiveAnc => _state with { AdaptiveAnc = enabled },
                RedmiSetting.AdaptiveSound => _state with { AdaptiveSound = enabled },
                _ => _state with { AutoAnswer = enabled }
            });
            return;
        }
        var transport = _transport;
        var address = _deviceAddress;
        var intent = Volatile.Read(ref _userIntent);
        _settingReadRevisions.TryGetValue(setting, out var before);
        bool CurrentSession() => !_disposed && !Paused && !_isOneMore && ReferenceEquals(transport, _transport)
            && address == _deviceAddress && intent == Volatile.Read(ref _userIntent);
        await _commandGate.WaitAsync(_lifetime.Token);
        try
        {
            if (!CurrentSession() || transport?.Connected != true) throw new InvalidOperationException("控制连接已变化，请重新读取设置");
            await transport.SendAsync(XiaomiProtocol.BuildSetSetting(setting, enabled, NextSequence()));
            await Task.Delay(100, _lifetime.Token);
            if (!CurrentSession()) throw new InvalidOperationException("控制连接已变化");
            await transport.SendAsync(XiaomiProtocol.BuildGetSetting(setting, NextSequence()));
        }
        finally { _commandGate.Release(); }
        for (var attempt = 0; attempt < 25; attempt++)
        {
            await Task.Delay(120, _lifetime.Token);
            if (!CurrentSession()) throw new InvalidOperationException("连接已中断，设置未确认");
            if (_settingReadRevisions.TryGetValue(setting, out var revision) && revision > before && ReadRedmiSetting(setting) == enabled) return;
        }
        Publish(setting switch
        {
            RedmiSetting.AdaptiveAnc => _state with { AdaptiveAnc = null },
            RedmiSetting.AdaptiveSound => _state with { AdaptiveSound = null },
            _ => _state with { AutoAnswer = null }
        });
        throw new TimeoutException("耳机未确认设置，请稍后重新读取");
    }
    private byte NextSequence() => ++_sequence;
    private void Publish(BudsState state) { if (_state == state) return; _state = state; StateChanged?.Invoke(this, state); }
    private static string FriendlyError(Exception ex) => ex.Message.Length > 96 ? ex.Message[..96] + "…" : ex.Message;

    public void Dispose()
    {
        _disposed = true; _lifetime.Cancel(); CloseTransport();
        // In-flight async operations still release their gates while shutdown completes.
    }
}
