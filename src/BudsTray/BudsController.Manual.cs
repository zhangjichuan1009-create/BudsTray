namespace RedmiBudsTray;

public sealed partial class BudsController
{
    private bool EffectiveAnc => PanelDeviceStore.Read().FirstOrDefault(d => d.Address == _deviceAddress)?.AncOverride ?? _profile?.Anc ?? true;
    private bool EffectiveTransparency => PanelDeviceStore.Read().FirstOrDefault(d => d.Address == _deviceAddress)?.TransparencyOverride ?? _profile?.Transparency ?? true;
    public async Task ConfigurePanelDeviceAsync(ulong address, EarphoneProtocol? protocol, bool? anc, bool? transparency)
    {
        var previous = PanelDeviceStore.Read().FirstOrDefault(d => d.Address == address) ?? throw new InvalidOperationException("设备已移除");
        CancelDeviceProbe(address);
        PanelDeviceStore.Configure(address, protocol, anc, transparency);
        if (_deviceAddress != address) return;
        Publish(_state with { AncSupported = EffectiveAnc, TransparencySupported = EffectiveTransparency });
        if (previous.ManualProtocol == protocol) return;
        Interlocked.Increment(ref _userIntent);
        CancelConnectionAttempt();
        await _connectionGate.WaitAsync();
        try
        {
            CloseTransport(); SetDevice(PanelDeviceStore.Choose(address)!);
            Publish(_state with { AncSupported = EffectiveAnc, TransparencySupported = EffectiveTransparency });
            _retryAfter = DateTime.MinValue;
        }
        finally { _connectionGate.Release(); }
        await ConnectAsync();
    }
    private async Task AttachSelectedProtocolAsync(PairedBluetoothDevice device, CancellationToken token)
    {
        var manual = PanelDeviceStore.Read().FirstOrDefault(d => d.Address == device.Address)?.ManualProtocol;
        if (!manual.HasValue) { await AttachProtocolAsync(device, token); return; }
        _isOneMore = manual == EarphoneProtocol.OneMore;
        _transport = _isOneMore
            ? await RfcommTransport.ConnectAsync(device.Address, Guid.Parse("00001101-0000-1000-8000-00805F9B34FB"), 0, token)
            : await RfcommTransport.ConnectWithFallbackAsync(device.Address, token);
        using var cancelled = token.Register(_transport.Dispose);
        if (_isOneMore)
        {
            _oneMore = new(); OneMoreReading? info = null;
            await _transport.SendAsync(OneMoreProtocol.InfoQuery);
            for (var i = 0; i < 4 && info is null; i++)
            {
                token.ThrowIfCancellationRequested();
                info = _oneMore.Feed(await _transport.ReceiveAsync(TimeSpan.FromMilliseconds(650))).FirstOrDefault(r => r.Command == 0x4E);
            }
            _profile = new(EarphoneProtocol.OneMore, info is not null, true);
        }
        else
        {
            await AuthenticateAsync();
            _profile = new(EarphoneProtocol.Xiaomi, true, false);
        }
        token.ThrowIfCancellationRequested();
        if (!PanelDeviceStore.Read().Any(d => d.Address == device.Address)) throw new OperationCanceledException("设备已移除");
        PanelDeviceStore.UpdateProfile(device.Address, _profile);
        Publish(_state with { Protocol = manual, IsHeadphone = _profile.IsHeadphone, AncSupported = EffectiveAnc, TransparencySupported = EffectiveTransparency });
        AppLog.Write($"Manual protocol selected: {manual}; replyConfirmed={_profile.Verified}");
    }
}
