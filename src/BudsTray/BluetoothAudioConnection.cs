using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RedmiBudsTray;

// Windows SDK: KSPROPSETID_BtAudio / KSPROPERTY_ONESHOT_*.
// Topology traversal follows the approach documented by ToothTray (BSD-2-Clause).
public static class BluetoothAudioConnection
{
    public sealed record Endpoint(string Id, string FilterId, uint State, Guid ContainerId);

    public static IReadOnlyList<Endpoint> GetEndpoints(ulong address)
    {
        var all = Enumerate();
        var direct = all.Where(x => x.FilterId.Contains(address.ToString("X12"), StringComparison.OrdinalIgnoreCase)).ToArray();
        var containers = direct.Select(x => x.ContainerId).Where(x => x != Guid.Empty).ToHashSet();
        // HFP filter paths do not contain the MAC address. Container ID joins them to A2DP safely.
        return all.Where(x => direct.Contains(x) || containers.Contains(x.ContainerId)).ToArray();
    }

    public static IReadOnlyList<Endpoint> Enumerate()
    {
        var result = new List<Endpoint>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection? collection = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(2, 15, out collection));
            collection.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null; object? topologyObject = null;
                try
                {
                    collection.Item(i, out device);
                    device.GetId(out var id); device.GetState(out var state);
                    var container = GetContainerId(device);
                    var iid = typeof(IDeviceTopology).GUID;
                    if (device.Activate(ref iid, 23, 0, out topologyObject) < 0) continue;
                    var topology = (IDeviceTopology)topologyObject;
                    topology.GetConnectorCount(out var connectors);
                    for (uint j = 0; j < connectors; j++)
                    {
                        IConnector? connector = null;
                        try
                        {
                            if (topology.GetConnector(j, out connector) >= 0 &&
                                connector.GetDeviceIdConnectedTo(out var filterId) >= 0 &&
                                filterId.Contains("bth", StringComparison.OrdinalIgnoreCase))
                                result.Add(new(id, filterId, state, container));
                        }
                        finally { Release(connector); }
                    }
                }
                catch (COMException) { /* Endpoint can disappear during a connection change. */ }
                finally { Release(topologyObject); Release(device); }
            }
        }
        finally { Release(collection); Release(enumerator); }
        return result;
    }

    public static bool IsConnected(ulong address)
    {
        var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>(), Address = address };
        return BluetoothGetDeviceInfo(0, ref info) == 0 && info.Connected != 0;
    }

    public static bool IsAudioReady(ulong address) => IsConnected(address) && GetEndpoints(address).Any(x => x.State == 1 && x.Id.StartsWith("{0.0.0.", StringComparison.OrdinalIgnoreCase));

    public static async Task EnsureConnectedAsync(ulong address, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Restoring paused PnP nodes recreates the audio endpoints asynchronously.
        for (var i = 0; i < 40 && await Task.Run(() => GetEndpoints(address).Count == 0, token); i++)
            await Task.Delay(250, token);
        if (await Task.Run(() => IsAudioReady(address), token)) return;
        AppLog.Write("Bluetooth audio reconnect requested");
        await Task.Run(() => RequestAudio(address, true), token);
        for (var i = 0; i < 60; i++)
        {
            token.ThrowIfCancellationRequested();
            if (await Task.Run(() => IsAudioReady(address), token)) return;
            await Task.Delay(250, token);
        }
        throw new TimeoutException("蓝牙音频连接未就绪，请确认耳机已开机且在附近");
    }

    public static async Task DisconnectAsync(ulong address, CancellationToken token)
    {
        // Audio driver receives the disconnect intent before the remaining ACL link is closed.
        AppLog.Write("Bluetooth audio disconnect requested");
        try { await Task.Run(() => { if (GetEndpoints(address).Count > 0) RequestAudio(address, false); }, token); }
        catch (Exception ex) when (ex is COMException or System.IO.FileNotFoundException or InvalidOperationException)
        { AppLog.Write("Audio disconnect request unavailable; continuing link disconnect: " + ex.Message); }
        // A KS request is asynchronous. Let the driver finish before considering ACL fallback.
        for (var i = 0; i < 16; i++)
        {
            await Task.Delay(250, token);
            if (!await Task.Run(() => IsConnected(address), token))
            {
                AppLog.Write("Bluetooth disconnected gracefully");
                return;
            }
        }
        AppLog.Write("Bluetooth ACL disconnect fallback (once)");
        await Task.Run(() => DisconnectLink(address), token);
        for (var i = 0; i < 40; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!await Task.Run(() => IsConnected(address), token)) return;
            await Task.Delay(250, token);
        }
        throw new TimeoutException("Windows 尚未断开耳机蓝牙连接");
    }

    private static void RequestAudio(ulong address, bool connect)
    {
        var endpoints = GetEndpoints(address);
        if (endpoints.Count == 0) throw new InvalidOperationException("未找到该耳机的蓝牙音频设备，请先在 Windows 中完成配对");
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        var accepted = false; var lastError = unchecked((int)0x80004005);
        try
        {
            foreach (var filter in endpoints.OrderBy(x => x.State == 1 ? 0 : 1).Select(x => x.FilterId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                IMMDevice? device = null; object? control = null;
                try
                {
                    Marshal.ThrowExceptionForHR(enumerator.GetDevice(filter, out device));
                    var iid = typeof(IKsControl).GUID;
                    Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, 0, out control));
                    var property = new KsProperty { Set = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D"), Id = connect ? 0u : 1u, Flags = 1 };
                    lastError = ((IKsControl)control).KsProperty(ref property, 24, 0, 0, out _);
                    AppLog.Write($"Audio {(connect ? "connect" : "disconnect")} {(filter.Contains("bthhfenum", StringComparison.OrdinalIgnoreCase) ? "HFP" : "A2DP")} result=0x{lastError:X8}");
                    accepted |= lastError >= 0;
                }
                catch (Exception ex) when (ex is COMException or System.IO.FileNotFoundException)
                { lastError = ex.HResult; AppLog.Write($"Audio driver endpoint skipped: 0x{lastError:X8}"); }
                finally { Release(control); Release(device); }
            }
        }
        finally { Release(enumerator); }
        if (!accepted) throw new COMException("Windows 音频驱动未接受蓝牙连接操作", lastError);
    }

    private static void DisconnectLink(ulong address)
    {
        if (!IsConnected(address)) return;
        uint size = 4;
        var find = BluetoothFindFirstRadio(ref size, out var radio);
        if (find == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "未找到蓝牙适配器");
        var lastError = 0;
        try
        {
            do
            {
                try
                {
                    if (DeviceIoControl(radio, 0x41000c, ref address, 8, 0, 0, out _, 0)) return;
                    lastError = Marshal.GetLastWin32Error();
                }
                finally { CloseHandle(radio); }
            } while (BluetoothFindNextRadio(find, out radio));
        }
        finally { BluetoothFindRadioClose(find); }
        if (IsConnected(address)) throw new Win32Exception(lastError, "蓝牙断开请求失败");
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    public static string? GetDefaultOutput(int role = 1)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice? device = null;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(0, role, out device) < 0) return null;
            Marshal.ThrowExceptionForHR(device.GetId(out var id));
            return id;
        }
        finally { Release(device); Release(enumerator); }
    }

    public static void SetDefaultOutput(string endpointId)
    {
        var previous = Enumerable.Range(0, 3).Select(GetDefaultOutput).ToArray();
        var policy = (IPolicyConfig)new PolicyConfigClient();
        var changed = 0;
        try
        {
            for (var role = 0; role < 3; role++)
            {
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(endpointId, role));
                changed++;
                if (GetDefaultOutput(role) != endpointId) throw new InvalidOperationException("Windows 未确认声音输出切换");
            }
        }
        catch
        {
            for (var role = 0; role < changed; role++)
                if (previous[role] is string old) policy.SetDefaultEndpoint(old, role);
            throw;
        }
        finally { Release(policy); }
    }

    public static void SelectOutput(ulong address)
    {
        SetDefaultOutput(GetOutputEndpoint(address));
    }

    private static string GetOutputEndpoint(ulong address) => GetEndpoints(address)
        .Where(x => x.State == 1 && x.Id.StartsWith("{0.0.0.", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.FilterId.Contains("bthhfenum", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
        .FirstOrDefault()?.Id ?? throw new InvalidOperationException("耳机的声音输出尚未就绪");

    public sealed record VolumeState(float Level, bool Muted);
    public static VolumeState GetVolume(ulong address) => WithVolume(address, volume =>
    {
        Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out var level));
        Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
        return new VolumeState(level, muted);
    });
    public static void SetVolume(ulong address, float level) => WithVolume(address, volume =>
    {
        Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 1), 0));
        return 0;
    });
    public static void SetMuted(ulong address, bool muted) => WithVolume(address, volume =>
    {
        Marshal.ThrowExceptionForHR(volume.SetMute(muted, 0));
        return 0;
    });
    private static T WithVolume<T>(ulong address, Func<IAudioEndpointVolume, T> action)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice? device = null; object? control = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(GetOutputEndpoint(address), out device));
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, 0, out control));
            return action((IAudioEndpointVolume)control);
        }
        finally { Release(control); Release(device); Release(enumerator); }
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint callback);
        [PreserveSig] int UnregisterControlChangeNotify(nint callback);
        [PreserveSig] int GetChannelCount(out uint channels);
        [PreserveSig] int SetMasterVolumeLevel(float level, nint context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, nint context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, nint context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, nint context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, nint context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] private class PolicyConfigClient { }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(nint id, nint format);
        [PreserveSig] int GetDeviceFormat(nint id, int defaultFormat, nint format);
        [PreserveSig] int ResetDeviceFormat(nint id);
        [PreserveSig] int SetDeviceFormat(nint id, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod(nint id, int defaultPeriod, nint period, nint minimum);
        [PreserveSig] int SetProcessingPeriod(nint id, nint period);
        [PreserveSig] int GetShareMode(nint id, nint mode);
        [PreserveSig] int SetShareMode(nint id, nint mode);
        [PreserveSig] int GetPropertyValue(nint id, nint key, nint value);
        [PreserveSig] int SetPropertyValue(nint id, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(nint id, int visible);
    }

    private static Guid GetContainerId(IMMDevice device)
    {
        IPropertyStore? store = null;
        var value = new PropVariant();
        try
        {
            if (device.OpenPropertyStore(0, out store) < 0) return Guid.Empty;
            var key = new PropertyKey { Format = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 };
            if (store.GetValue(ref key, out value) < 0 || value.Type != 72 || value.Pointer == 0) return Guid.Empty;
            return Marshal.PtrToStructure<Guid>(value.Pointer);
        }
        finally { PropVariantClear(ref value); Release(store); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Pointer; }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [StructLayout(LayoutKind.Explicit, Size = 560)]
    private struct DeviceInfo
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(8)] public ulong Address;
        [FieldOffset(20)] public int Connected;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KsProperty { public Guid Set; public uint Id, Flags; }
    [DllImport("bthprops.cpl")] private static extern uint BluetoothGetDeviceInfo(nint radio, ref DeviceInfo info);
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern nint BluetoothFindFirstRadio(ref uint parameters, out nint radio);
    [DllImport("bthprops.cpl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindNextRadio(nint find, out nint radio);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindRadioClose(nint find);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(nint handle, uint code, ref ulong input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection { [PreserveSig] int GetCount(out uint count); [PreserveSig] int Item(uint index, out IMMDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int OpenPropertyStore(uint mode, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
    }
    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetConnectorType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected(out int connected);
        [PreserveSig] int GetConnectedTo(out IConnector other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl { [PreserveSig] int KsProperty(ref KsProperty property, uint size, nint data, uint dataSize, out uint returned); }
}
