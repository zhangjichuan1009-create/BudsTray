using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RedmiBudsTray;

public sealed record PairedBluetoothDevice(string Name, ulong Address);

public static class BluetoothDeviceFinder
{
    private const string DevicesKey = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";

    public static PairedBluetoothDevice? FindRedmiBuds8Pro() => FindAll().FirstOrDefault(d =>
        Normalize(d.Name).Contains("redmibuds8pro"));

    public static PairedBluetoothDevice? FindSupported(ulong? preferred = null)
    {
        var devices = FindSupportedDevices();
        if (preferred.HasValue) return devices.FirstOrDefault(d => d.Address == preferred.Value);
        return DeviceProtocols.Choose(devices, preferred, BluetoothAudioConnection.IsAudioReady);
    }
    public static IReadOnlyList<PairedBluetoothDevice> FindSupportedDevices() => DeviceProtocols.Candidates(FindAll());

    public static PairedBluetoothDevice? FindXiaomiEarbuds() => FindAll().FirstOrDefault(d =>
    {
        var n = Normalize(d.Name);
        return n.Contains("redmi") || n.Contains("xiaomi") || n.Contains("小米");
    });

    public static IReadOnlyList<PairedBluetoothDevice> FindAll()
    {
        var result = new List<PairedBluetoothDevice>();
        using var root = Registry.LocalMachine.OpenSubKey(DevicesKey);
        if (root is null) return result;
        foreach (var subName in root.GetSubKeyNames().Where(x => x.Length == 12))
        {
            using var sub = root.OpenSubKey(subName);
            var name = DecodeName(sub?.GetValue("Name")) ?? DecodeName(sub?.GetValue("LEName"));
            if (string.IsNullOrWhiteSpace(name) || !ulong.TryParse(subName, System.Globalization.NumberStyles.HexNumber, null, out var address)) continue;
            result.Add(new(name, address));
        }
        return result;
    }

    private static string? DecodeName(object? value) => value switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim(),
        string text => text.TrimEnd('\0').Trim(),
        _ => null
    };
    private static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

public sealed class RfcommTransport : IDisposable
{
    private const int AF_BTH = 32, SOCK_STREAM = 1, BTHPROTO_RFCOMM = 3, WSAEWOULDBLOCK = 10035;
    private const int FIONBIO = unchecked((int)0x8004667E);
    private static bool _winsockStarted;
    private readonly object _sendLock = new();
    private nint _socket = new(-1);
    public ulong Address { get; }
    public bool Connected => _socket != new nint(-1);

    private RfcommTransport(ulong address) => Address = address;

    public static async Task<RfcommTransport> ConnectWithFallbackAsync(ulong address, CancellationToken token = default)
    {
        Exception? first = null;
        try { return await ConnectAsync(address, Guid.Parse("0000FD2D-0000-1000-8000-00805F9B34FB"), 0, token); }
        catch (Exception ex) { first = ex; }
        token.ThrowIfCancellationRequested();
        try { return await ConnectAsync(address, Guid.Empty, 6, token); }
        catch (Exception ex) { throw new InvalidOperationException($"无法打开耳机控制通道（SDP 与通道 6 均失败，WSA {Marshal.GetLastWin32Error()}）。{first?.Message}", ex); }
    }

    public static async Task<RfcommTransport> ConnectAsync(ulong address, Guid service, uint port, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        return await Task.Run(() => Connect(address, service, port, deadline.Token));
    }

    private static RfcommTransport Connect(ulong address, Guid service, uint port, CancellationToken token)
    {
        EnsureWinsock();
        var transport = new RfcommTransport(address);
        var socket = Native.socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM);
        if (socket == new nint(-1)) throw new InvalidOperationException($"无法创建蓝牙套接字（WSA {Native.WSAGetLastError()}）");
        transport._socket = socket;
        using var cancellation = token.Register(transport.Dispose);
        token.ThrowIfCancellationRequested();
        var endpoint = new SockAddrBth { AddressFamily = AF_BTH, BluetoothAddress = address, ServiceClassId = service, Port = port };
        if (Native.connect(socket, ref endpoint, Marshal.SizeOf<SockAddrBth>()) != 0)
        {
            var error = Native.WSAGetLastError(); transport.Dispose();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"RFCOMM 连接失败（WSA {error}）");
        }
        uint nonBlocking = 1;
        Native.ioctlsocket(socket, FIONBIO, ref nonBlocking);
        token.ThrowIfCancellationRequested();
        return transport;
    }

    public Task SendAsync(byte[] data) => Task.Run(() =>
    {
        if (!Connected) throw new InvalidOperationException("耳机未连接");
        lock (_sendLock)
        {
            var sent = Native.send(_socket, data, data.Length, 0);
            if (sent != data.Length) throw new InvalidOperationException($"发送失败（WSA {Native.WSAGetLastError()}）");
        }
    });

    public Task<byte[]> ReceiveAsync(TimeSpan timeout) => Task.Run(() =>
    {
        if (!Connected) return Array.Empty<byte>();
        var output = new List<byte>();
        var buffer = new byte[1024];
        var until = DateTime.UtcNow + timeout;
        var lastData = DateTime.MinValue;
        while (DateTime.UtcNow < until)
        {
            // Wait in Winsock instead of waking every 15 ms while the buds are idle.
            var waitMs = Math.Max(1, Math.Min((until - DateTime.UtcNow).TotalMilliseconds, output.Count > 0 ? 35 : 600));
            var readable = new SocketSet { Count = 1, Socket = _socket };
            var wait = new SocketTimeout { Microseconds = (int)(waitMs * 1000) };
            var ready = Native.select(0, ref readable, 0, 0, ref wait);
            if (ready == 0) break;
            if (ready < 0) throw new IOException($"控制通道等待失败（WSA {Native.WSAGetLastError()}）");
            var count = Native.recv(_socket, buffer, buffer.Length, 0);
            if (count > 0) { output.AddRange(buffer.AsSpan(0, count).ToArray()); lastData = DateTime.UtcNow; continue; }
            var error = Native.WSAGetLastError();
            if (count < 0 && error == WSAEWOULDBLOCK)
            {
                if (output.Count > 0 && DateTime.UtcNow - lastData > TimeSpan.FromMilliseconds(35)) break;
                continue;
            }
            if (count == 0) throw new IOException("耳机关闭了控制通道");
            throw new IOException($"控制通道读取失败（WSA {error}）");
        }
        return output.ToArray();
    });

    private static void EnsureWinsock()
    {
        if (_winsockStarted) return;
        var data = new byte[512];
        if (Native.WSAStartup(0x0202, data) != 0) throw new InvalidOperationException("Windows 蓝牙网络初始化失败");
        _winsockStarted = true;
    }

    public void Dispose()
    {
        var socket = Interlocked.Exchange(ref _socket, new nint(-1));
        if (socket != new nint(-1)) Native.closesocket(socket);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SockAddrBth
    {
        public ushort AddressFamily;
        public ulong BluetoothAddress;
        public Guid ServiceClassId;
        public uint Port;
    }

    [StructLayout(LayoutKind.Sequential)] private struct SocketSet { public uint Count; public nint Socket; }
    [StructLayout(LayoutKind.Sequential)] private struct SocketTimeout { public int Seconds; public int Microseconds; }

    private static class Native
    {
        [DllImport("ws2_32.dll")] public static extern int select(int ignored, ref SocketSet read, nint write, nint error, ref SocketTimeout timeout);
        [DllImport("ws2_32.dll")] public static extern int WSAStartup(ushort version, byte[] data);
        [DllImport("ws2_32.dll")] public static extern int WSAGetLastError();
        [DllImport("ws2_32.dll", SetLastError = true)] public static extern nint socket(int family, int type, int protocol);
        [DllImport("ws2_32.dll", SetLastError = true)] public static extern int connect(nint socket, ref SockAddrBth name, int nameLength);
        [DllImport("ws2_32.dll")] public static extern int send(nint socket, byte[] buffer, int length, int flags);
        [DllImport("ws2_32.dll")] public static extern int recv(nint socket, byte[] buffer, int length, int flags);
        [DllImport("ws2_32.dll")] public static extern int ioctlsocket(nint socket, int command, ref uint argument);
        [DllImport("ws2_32.dll")] public static extern int closesocket(nint socket);
    }
}
