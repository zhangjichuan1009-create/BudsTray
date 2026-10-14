namespace RedmiBudsTray;

public sealed partial class BudsController
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, CancellationTokenSource> _deviceProbes = new();
    private void CancelDeviceProbe(ulong address)
    {
        if (!_deviceProbes.TryGetValue(address, out var pending)) return;
        try { pending.Cancel(); } catch (ObjectDisposedException) { }
    }
    private async Task InspectAddedDeviceAsync(PairedBluetoothDevice device)
    {
        if (IsDemo || ConnectionPreferences.IsPaused(device.Address) || !await Task.Run(() => BluetoothAudioConnection.IsAudioReady(device.Address))) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        _deviceProbes[device.Address] = deadline;
        try
        {
            using var inspector = new BudsController();
            using var closing = deadline.Token.Register(inspector.Dispose);
            inspector._deviceAddress = device.Address;
            inspector.SetDevice(device);
            await inspector.AttachProtocolAsync(device, deadline.Token);
            inspector.StartReader();
            await inspector.SendStateQueriesAsync();
            for (var i = 0; i < 6 && !inspector.State.NoiseMode.HasValue; i++) await Task.Delay(180, deadline.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write("Added-device inspection: " + ex.Message);
            PanelDeviceStore.MarkDetectionFailed(device.Address);
        }
        finally { _deviceProbes.TryRemove(device.Address, out _); }
    }
    private void PersistObservedProfile()
    {
        if (_profile is null || _deviceAddress is not ulong address) return;
        var registered = PanelDeviceStore.Read().FirstOrDefault(d => d.Address == address);
        if (registered is not null && (registered.Profile != _profile || !registered.ProtocolConfirmed)) PanelDeviceStore.UpdateProfile(address, _profile);
    }
    public async Task AddPanelDeviceAsync(PairedBluetoothDevice device)
    {
        PanelDeviceStore.Add(device);
        if (_deviceAddress.HasValue) { await InspectAddedDeviceAsync(device); return; }
        _deviceAddress = device.Address;
        if (!IsDemo)
        {
            var paused = ConnectionPreferences.IsPaused(device.Address);
            ConnectionPreferences.SelectedAddress = device.Address;
            ConnectionPreferences.SetPaused(device.Address, paused);
        }
        SetDevice(device);
        _manualDisconnect = false;
        _retryAfter = DateTime.MinValue;
        Publish(_state with { Hint = "设备已添加" });
        // Adding does not reconnect Bluetooth or change the audio output.
        await ConnectAsync();
    }

    public async Task RemovePanelDeviceAsync(ulong address)
    {
        CancelDeviceProbe(address);
        PanelDeviceStore.Remove(address);
        if (_deviceAddress != address) return;
        Interlocked.Increment(ref _userIntent);
        _manualDisconnect = true;
        CancelConnectionAttempt();
        CloseTransport();
        await _manualGate.WaitAsync();
        _manualOperation = true;
        try
        {
            await _connectionGate.WaitAsync();
            try
            {
                CloseTransport();
                var next = PanelDeviceStore.Choose();
                _deviceAddress = next?.Address;
                if (!IsDemo)
                {
                    var paused = next is not null && ConnectionPreferences.IsPaused(next.Address);
                    ConnectionPreferences.SelectedAddress = _deviceAddress;
                    if (next is not null) ConnectionPreferences.SetPaused(next.Address, paused);
                }
                _profile = null;
                _isOneMore = false;
                var empty = new BudsState("蓝牙耳机", "未连接", false, false, Hint: "请添加耳机");
                if (next is null) Publish(empty);
                else
                {
                    SetDevice(next);
                    Publish(_state with { Hint = "设备已移除" });
                }
                _retryAfter = DateTime.MinValue;
            }
            finally { _connectionGate.Release(); }
        }
        finally { _manualDisconnect = false; _manualOperation = false; _manualGate.Release(); }
    }

    private async Task AttachProtocolAsync(PairedBluetoothDevice device, CancellationToken token)
    {
        var hint = _profile;
        var families = hint?.Protocol == EarphoneProtocol.OneMore
            ? new[] { EarphoneProtocol.OneMore, EarphoneProtocol.Xiaomi }
            : new[] { EarphoneProtocol.Xiaomi, EarphoneProtocol.OneMore };
        foreach (var family in families)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                _isOneMore = family == EarphoneProtocol.OneMore;
                _transport = _isOneMore
                    ? await RfcommTransport.ConnectAsync(device.Address, Guid.Parse("00001101-0000-1000-8000-00805F9B34FB"), 0, token)
                    : await RfcommTransport.ConnectWithFallbackAsync(device.Address, token);
                using var cancelled = token.Register(_transport.Dispose);
                if (_isOneMore)
                {
                    _oneMore = new();
                    await _transport.SendAsync(OneMoreProtocol.InfoQuery);
                    OneMoreReading? info = null;
                    for (var i = 0; i < 4 && info is null; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        info = _oneMore.Feed(await _transport.ReceiveAsync(TimeSpan.FromMilliseconds(650))).FirstOrDefault(r => r.Command == 0x4E);
                    }
                    if (info is null) throw new InvalidOperationException("万魔协议未返回有效信息");
                    AppLog.Write($"OneMore information validated: firmware={info.Firmware}; battery={info.Battery}");
                    _profile = hint is { Protocol: EarphoneProtocol.OneMore } ? hint : new(EarphoneProtocol.OneMore, false, true);
                }
                else
                {
                    await AuthenticateAsync();
                    _profile = hint is { Protocol: EarphoneProtocol.Xiaomi } ? hint : new(EarphoneProtocol.Xiaomi, false, false);
                    await _transport.SendAsync(XiaomiProtocol.BuildDeviceInfoQuery(NextSequence()));
                    for (var i = 0; i < 3; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var packets = await ReceiveHandshakeAsync(TimeSpan.FromMilliseconds(600));
                        var info = packets.Select(XiaomiProtocol.TryReadDeviceInfo).FirstOrDefault(x => x is not null);
                        if (info is null) continue;
                        _profile = DeviceProtocols.FromXiaomiInfo(info, _profile);
                        AppLog.Write($"Xiaomi information validated: firmware={info.Firmware}; vid={info.VendorId:X4}; pid={info.ProductId:X4}");
                        break;
                    }
                }
                token.ThrowIfCancellationRequested();
                if (!PanelDeviceStore.Read().Any(d => d.Address == device.Address)) throw new OperationCanceledException("设备已从面板移除");
                PanelDeviceStore.UpdateProfile(device.Address, _profile!);
                Publish(_state with { IsHeadphone = _profile!.IsHeadphone, Protocol = family,
                    AncSupported = _profile.Anc, TransparencySupported = _profile.Transparency });
                AppLog.Write($"Device protocol detected: {family}; anc={_profile.Anc}; transparency={_profile.Transparency}");
                return;
            }
            catch (OperationCanceledException) { CloseTransport(); throw; }
            catch (Exception ex)
            {
                AppLog.Write($"Protocol detection {family}: {ex.GetType().Name}: {ex.Message}");
                CloseTransport();
                token.ThrowIfCancellationRequested();
            }
        }
        _profile = hint;
        _isOneMore = hint?.Protocol == EarphoneProtocol.OneMore;
        PanelDeviceStore.MarkDetectionFailed(device.Address);
        throw new InvalidOperationException("暂未识别小米或万魔协议，请确认耳机已连接且未被其他管理应用占用");
    }
}
